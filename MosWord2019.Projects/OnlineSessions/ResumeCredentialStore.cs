using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MosWord2019.Core.IO;
using MosWord2019.Projects.OnlinePackages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace MosWord2019.Projects.OnlineSessions
{
    public sealed class ResumeCredentialStore
    {
        private readonly string root,path,machine;
        private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("MosWord2019.OnlineSession.v1");
        public ResumeCredentialStore(string root=null,string machineId=null)
        {
            this.root=Path.GetFullPath(root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MosWord2019","OnlineSession"));
            for(string p=this.root;!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                if(Directory.Exists(Path.Combine(p,".git")) || File.Exists(Path.Combine(p,".git"))) throw new ArgumentException("Session credentials must be stored outside a repository.");
            machine=machineId??MachineIdentity.GetMachineId(); if(!OnlineSessionClient.ValidMachine(machine)) throw new ArgumentException("Invalid machine identity.");
            path=Path.Combine(this.root,"session.bin");
        }
        private void Prepare()
        {
            OnlinePackageCache.CreatePrivateDirectory(root);
            var acl=new DirectorySecurity(); var sid=WindowsIdentity.GetCurrent().User;
            acl.SetOwner(sid); acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(root,acl); OnlinePackageCache.RejectReparse(path);
        }
        public void Save(OnlineSessionCredentials credentials)
        {
            if(credentials==null || credentials.SessionId==Guid.Empty || !string.Equals(credentials.MachineId,machine,StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(credentials.ResumeToken) || string.IsNullOrWhiteSpace(credentials.SessionToken)) throw new ArgumentException("Invalid session credentials.");
            Prepare(); byte[] plain=Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(credentials)),encrypted;
            try { if(plain.Length>65536) throw new InvalidDataException("Session credentials exceed the size limit."); encrypted=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser); } finally { Array.Clear(plain,0,plain.Length); }
            string temp=Path.Combine(root,Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { output.Write(encrypted,0,encrypted.Length); output.Flush(true); }
                AtomicFile.Commit(temp,path,File.Exists(path),()=> { OnlinePackageCache.RejectReparse(root); OnlinePackageCache.RejectReparse(path); });
                var acl=new FileSecurity(); var sid=WindowsIdentity.GetCurrent().User; acl.SetOwner(sid); acl.SetAccessRuleProtection(true,false); acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow)); File.SetAccessControl(path,acl);
            }
            finally { OnlinePackageCache.RejectReparse(temp); if(File.Exists(temp)) File.Delete(temp); }
        }
        public OnlineSessionCredentials Load()
        {
            OnlinePackageCache.RejectReparse(path); if(!File.Exists(path)) return null;
            byte[] encrypted;
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            { if(input.Length==0 || input.Length>131072) throw new InvalidDataException("Invalid session credentials."); encrypted=new byte[(int)input.Length]; int p=0,n; while(p<encrypted.Length && (n=input.Read(encrypted,p,encrypted.Length-p))>0) p+=n; if(p!=encrypted.Length) throw new EndOfStreamException(); }
            byte[] plain=ProtectedData.Unprotect(encrypted,Entropy,DataProtectionScope.CurrentUser);
            try
            {
                using(var text=new StringReader(new UTF8Encoding(false,true).GetString(plain)))
                using(var reader=new JsonTextReader(text) { MaxDepth=8,DateParseHandling=DateParseHandling.DateTimeOffset })
                {
                    var json=JObject.Load(reader,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error }); if(reader.Read()) throw new InvalidDataException("Invalid session credentials.");
                    var value=json.ToObject<OnlineSessionCredentials>();
                    if(value==null || value.SessionId==Guid.Empty || !string.Equals(value.MachineId,machine,StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value.ResumeToken) || string.IsNullOrWhiteSpace(value.SessionToken) || (value.Mode!=null && value.Mode!="Training" && value.Mode!="Testing") || value.RemainingUses<0) throw new InvalidDataException("Session credentials do not match this machine.");
                    return value;
                }
            }
            finally { Array.Clear(plain,0,plain.Length); }
        }
        public void Delete() { OnlinePackageCache.RejectReparse(path); if(File.Exists(path)) File.Delete(path); }
    }
}
