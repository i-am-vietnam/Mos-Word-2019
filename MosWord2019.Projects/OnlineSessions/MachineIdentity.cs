using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
namespace MosWord2019.Projects.OnlineSessions
{
    public static class MachineIdentity
    {
        public static string GetMachineId()
        {
            using(var key=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var cryptography=key.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography",false))
            {
                string raw=cryptography?.GetValue("MachineGuid") as string; Guid guid;
                if(!Guid.TryParse(raw,out guid)) throw new InvalidOperationException("Machine identity is unavailable.");
                using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(guid.ToString("D").ToLowerInvariant()))).Replace("-","").ToLowerInvariant();
            }
        }
    }
}
