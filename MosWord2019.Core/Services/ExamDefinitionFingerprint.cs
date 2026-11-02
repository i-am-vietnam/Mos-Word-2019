using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public static class ExamDefinitionFingerprint
    {
        public static string Compute(ExamDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write("MosWord2019.Exam.v1");
                    writer.Write(definition.Seed);
                    writer.Write(definition.Projects.Count);
                    foreach (var project in definition.Projects)
                    {
                        writer.Write(project.ProjectId);
                        writer.Write(project.TaskIds.Count);
                        foreach (string task in project.TaskIds)
                        { writer.Write(task); writer.Write(project.InstructionKeys[task]); }
                    }
                }
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
