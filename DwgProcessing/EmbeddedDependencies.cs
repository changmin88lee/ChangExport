using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace ChangExport.DwgProcessing;

internal static class EmbeddedDependencies
{
    // The managed DWG engine travels inside ChangExport.dll; no CAD installation,
    // download, registry lookup or executable is required on the user's machine.
#pragma warning disable CA2255 // Deliberate assembly bootstrap for an embedded, privately shipped dependency.
    [ModuleInitializer]
    internal static void Initialize() => AssemblyLoadContext.Default.Resolving += Resolve;
#pragma warning restore CA2255

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name != "ACadSharp" || name.Version != new Version(3, 7, 1, 0)) return null;
        using Stream resource = typeof(EmbeddedDependencies).Assembly.GetManifestResourceStream("ChangExport.Dependencies.ACadSharp.dll")
            ?? throw new FileNotFoundException("창Export에 포함된 DWG 처리 모듈이 없습니다. 최신 설치본을 확인하세요.");
        return context.LoadFromStream(resource);
    }
}
