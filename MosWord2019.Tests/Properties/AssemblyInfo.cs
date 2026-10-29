using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: AssemblyTitle("MosWord2019.Tests")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: ComVisible(false)]
[assembly: TestDataSourceDiscovery(TestDataSourceDiscoveryOption.DuringDiscovery)]
[assembly: DoNotParallelize]

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MosWord2019.Desktop.Tests")]
