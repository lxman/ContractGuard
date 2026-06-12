using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ContractGuard.Core.Metadata;

/// <summary>
/// Resolves members to source file/line from the portable PDB - embedded in the PE, or the
/// sibling .pdb next to the assembly. Best-effort by design: no PDB, no locations, and the
/// gate's verdict is unchanged either way.
/// </summary>
internal sealed class SourceLocator : IDisposable
{
    private readonly MetadataReaderProvider _provider;
    private readonly MetadataReader _pdb;

    private SourceLocator(MetadataReaderProvider provider)
    {
        _provider = provider;
        _pdb = provider.GetMetadataReader();
    }

    public static SourceLocator? TryCreate(PEReader pe, string? assemblyPath)
    {
        try
        {
            foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                    return new SourceLocator(pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry));
            }

            if (assemblyPath is not null)
            {
                string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    return new SourceLocator(MetadataReaderProvider.FromPortablePdbStream(
                        new MemoryStream(File.ReadAllBytes(pdbPath))));
                }
            }
        }
        catch (BadImageFormatException)
        {
            // Windows-style (non-portable) PDB or corrupt debug data: locations stay null.
        }

        return null;
    }

    /// <summary>First visible sequence point of the method, as "path(line)".</summary>
    public string? Find(MethodDefinitionHandle handle)
    {
        try
        {
            MethodDebugInformation info = _pdb.GetMethodDebugInformation(handle.ToDebugInformationHandle());
            if (info.SequencePointsBlob.IsNil)
                return null;

            foreach (SequencePoint point in info.GetSequencePoints())
            {
                if (point.IsHidden || point.Document.IsNil)
                    continue;

                Document document = _pdb.GetDocument(point.Document);
                return $"{_pdb.GetString(document.Name)}({point.StartLine})";
            }
        }
        catch (BadImageFormatException)
        {
        }

        return null;
    }

    public void Dispose() => _provider.Dispose();
}
