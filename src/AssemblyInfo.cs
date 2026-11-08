using System.Reflection;
using System.Runtime.Versioning;

// Hedef çerçeve bildirilmezse .NET eski uyumluluk davranışlarına düşer (ör. TLS 1.0, ikon/API indirmeleri başarısız olur).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("ValoLive")]
[assembly: AssemblyProduct("ValoLive")]
[assembly: AssemblyVersion("1.3.2.0")]
