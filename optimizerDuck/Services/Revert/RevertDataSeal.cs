using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Revert;

namespace optimizerDuck.Services.Revert;

/// <summary>
///     Seals revert data with an HMAC so a process without administrator rights cannot plant or
///     edit the steps an elevated revert later runs. The key lives in a registry key only
///     Administrators and SYSTEM can read; the revert directory itself is writable by the user.
/// </summary>
internal static class RevertDataSeal
{
    internal const string KeyPath = @"SOFTWARE\optimizerDuck";
    internal const string KeyValueName = "RevertSealKey";
    private const int KeyLength = 32;

    private static readonly Lock KeyLock = new();
    private static byte[]? _key;

    /// <summary>Serializes the data indented with a fresh signature, ready to be written.</summary>
    internal static string ToJson(RevertData data)
    {
        return ToJson(data, GetKey());
    }

    private static string ToJson(RevertData data, byte[] key)
    {
        data.Signature = null;
        var canonical = Parse(JsonConvert.SerializeObject(data, Formatting.None))!;
        data.Signature = Convert.ToBase64String(Mac(key, canonical.ToString(Formatting.None)));
        return JsonConvert.SerializeObject(data, Formatting.Indented);
    }

    /// <summary>Whether the file text carries a signature that matches its content.</summary>
    internal static bool Verify(string json)
    {
        var token = Parse(json);
        if (token is not JObject obj)
            return false;

        if (obj[nameof(RevertData.Signature)] is not JValue { Type: JTokenType.String } sigToken)
            return false;

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String((string)sigToken!);
        }
        catch (FormatException)
        {
            return false;
        }

        obj.Remove(nameof(RevertData.Signature));
        var actual = Mac(GetKey(), obj.ToString(Formatting.None));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    // Dates stay strings, so a file read back reproduces the exact
    // canonical text it was signed from.
    private static JToken? Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
        };
        try
        {
            return JToken.ReadFrom(reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] Mac(byte[] key, string canonical)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
    }

    private static byte[] GetKey()
    {
        lock (KeyLock)
        {
            if (_key is not null)
                return _key;

            using var key =
                Registry.LocalMachine.OpenSubKey(KeyPath, writable: true)
                ?? Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            ProtectKey(key);

            if (key.GetValue(KeyValueName) is byte[] { Length: KeyLength } existing)
            {
                _key = existing;
                return _key;
            }

            var created = RandomNumberGenerator.GetBytes(KeyLength);
            key.SetValue(KeyValueName, created, RegistryValueKind.Binary);

            // The key did not exist, so this build is the first to seal: every revert file
            // already on disk was written before sealing existed and is trusted once.
            SealExistingFiles(created);
            _key = created;
            return _key;
        }
    }

    /// <summary>
    ///     Restricts the key to Administrators and SYSTEM. Users can read HKLM\SOFTWARE by
    ///     default, and the inherited entry would hand them the key.
    /// </summary>
    private static void ProtectKey(RegistryKey key)
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (
            var sid in new[]
            {
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            }
        )
            security.AddAccessRule(
                new RegistryAccessRule(
                    sid,
                    RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
        key.SetAccessControl(security);
    }

    private static void SealExistingFiles(byte[] key)
    {
        if (!Directory.Exists(Shared.RevertDirectory))
            return;

        foreach (var path in Directory.GetFiles(Shared.RevertDirectory, "*.json"))
        {
            try
            {
                var data = JsonConvert.DeserializeObject<RevertData>(File.ReadAllText(path));
                if (data is null || data.Signature is not null)
                    continue;
                File.WriteAllText(path, ToJson(data, key));
            }
            catch (Exception ex)
            {
                Serilog
                    .Log.ForContext(typeof(RevertDataSeal))
                    .Warning(ex, "Could not seal existing revert file {Path}", path);
            }
        }
    }

    /// <summary>Makes sure the key exists, sealing any legacy file once. Called at startup.</summary>
    internal static void EnsureKey(ILogger logger)
    {
        try
        {
            _ = GetKey();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Revert data sealing key is unavailable");
        }
    }
}
