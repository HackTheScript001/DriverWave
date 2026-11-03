using System.Management;
using Microsoft.Win32;

namespace DriverWave.Services;

/// <summary>Creates a System Restore point before any drivers are installed, so changes can be undone.</summary>
public static class RestorePoint
{
    private const int DeviceDriverInstall = 10;
    private const int BeginSystemChange = 100;

    public static bool TryCreate(string description)
    {
        try
        {
            // Windows normally only allows one restore point per 24h; lift that for this call.
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"))
                key?.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);

            var scope = new ManagementScope(@"\\.\root\default");
            using var sr = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);

            // System Restore is often switched off on fresh installs – turn it on for the system drive.
            try
            {
                var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";
                using var enableArgs = sr.GetMethodParameters("Enable");
                enableArgs["Drive"] = systemDrive;
                enableArgs["WaitTillEnabled"] = true;
                sr.InvokeMethod("Enable", enableArgs, null);
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't enable System Restore: {ex.Message}");
            }

            using var args = sr.GetMethodParameters("CreateRestorePoint");
            args["Description"] = description;
            args["RestorePointType"] = DeviceDriverInstall;
            args["EventType"] = BeginSystemChange;
            using var result = sr.InvokeMethod("CreateRestorePoint", args, null);
            var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);

            Log.Write(code == 0 ? $"Restore point created: {description}" : $"Restore point failed (code {code})");
            return code == 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Restore point failed: {ex.Message}");
            return false;
        }
    }
}
