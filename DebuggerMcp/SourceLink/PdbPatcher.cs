using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.SourceLink;

/// <summary>
/// Utility for patching PDB files to match DLL GUIDs when there's a version mismatch.
/// This is used when downloading symbols by version tag instead of exact commit SHA.
/// </summary>
public class PdbPatcher
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the PdbPatcher class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public PdbPatcher(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Result of a PDB patching operation.
    /// </summary>
    public class PatchResult
    {
        /// <summary>
        /// Whether the patching was successful.
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// The PDB file that was patched.
        /// </summary>
        public string PdbPath { get; set; } = string.Empty;
        
        /// <summary>
        /// The DLL file used as the source of the GUID.
        /// </summary>
        public string DllPath { get; set; } = string.Empty;
        
        /// <summary>
        /// The original GUID in the PDB.
        /// </summary>
        public Guid OriginalGuid { get; set; }
        
        /// <summary>
        /// The new GUID from the DLL (what we patched to).
        /// </summary>
        public Guid NewGuid { get; set; }
        
        /// <summary>
        /// Whether patching was needed (GUIDs were different).
        /// </summary>
        public bool WasPatched { get; set; }
        
        /// <summary>
        /// Whether the patch was verified by re-reading the PDB after patching.
        /// </summary>
        public bool Verified { get; set; }
        
        /// <summary>
        /// The GUID read from the PDB after patching (for verification).
        /// </summary>
        public Guid? VerifiedGuid { get; set; }
        
        /// <summary>
        /// Error message if patching failed.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Patches a PDB file to match the GUID of the corresponding DLL.
    /// </summary>
    /// <param name="dllPath">Path to the DLL file to read the GUID from.</param>
    /// <param name="pdbPath">Path to the PDB file to patch.</param>
    /// <returns>Result of the patching operation.</returns>
    public PatchResult PatchPdbToMatchDll(string dllPath, string pdbPath)
    {
        var result = new PatchResult
        {
            DllPath = dllPath,
            PdbPath = pdbPath
        };

        try
        {
            // Read the expected GUID from the DLL
            var dllGuid = ReadGuidFromDll(dllPath);
            if (dllGuid == null)
            {
                result.ErrorMessage = "Could not read GUID from DLL (no CodeView debug info)";
                _logger?.LogWarning("[PdbPatcher] {Error}: {Dll}", result.ErrorMessage, dllPath);
                return result;
            }
            result.NewGuid = dllGuid.Value;

            // Read the current GUID from the PDB
            var pdbGuid = ReadGuidFromPdb(pdbPath);
            if (pdbGuid == null)
            {
                result.ErrorMessage = "Could not read GUID from PDB";
                _logger?.LogWarning("[PdbPatcher] {Error}: {Pdb}", result.ErrorMessage, pdbPath);
                return result;
            }
            result.OriginalGuid = pdbGuid.Value;

            // Check if patching is needed
            if (dllGuid.Value == pdbGuid.Value)
            {
                _logger?.LogDebug("[PdbPatcher] GUIDs already match for {Pdb}", Path.GetFileName(pdbPath));
                result.Success = true;
                result.WasPatched = false;
                return result;
            }

            // Patch the PDB
            _logger?.LogInformation("[PdbPatcher] Patching {Pdb}: {OldGuid} -> {NewGuid}",
                Path.GetFileName(pdbPath), pdbGuid.Value, dllGuid.Value);

            var patched = PatchPdbGuid(pdbPath, dllGuid.Value);
            if (patched)
            {
                result.WasPatched = true;
                
                // Verify the patch by re-reading the PDB
                var verifiedGuid = ReadGuidFromPdb(pdbPath);
                result.VerifiedGuid = verifiedGuid;
                
                if (verifiedGuid == dllGuid.Value)
                {
                    result.Success = true;
                    result.Verified = true;
                    _logger?.LogInformation("[PdbPatcher] ✓ Verified {Pdb}: GUID now {Guid}",
                        Path.GetFileName(pdbPath), verifiedGuid);
                }
                else
                {
                    result.Success = false;
                    result.Verified = false;
                    result.ErrorMessage = $"Verification failed: expected {dllGuid.Value}, got {verifiedGuid}";
                    _logger?.LogError("[PdbPatcher] ✗ Verification failed for {Pdb}: expected {Expected}, got {Actual}",
                        Path.GetFileName(pdbPath), dllGuid.Value, verifiedGuid);
                }
            }
            else
            {
                result.ErrorMessage = "Failed to patch PDB file";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger?.LogError(ex, "[PdbPatcher] Error patching {Pdb}", pdbPath);
            return result;
        }
    }

    /// <summary>
    /// Reads the PDB GUID from a DLL's Debug Directory (CodeView record).
    /// </summary>
    /// <param name="dllPath">Path to the DLL file.</param>
    /// <returns>The GUID, or null if not found.</returns>
    public Guid? ReadGuidFromDll(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);

            foreach (var entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
                    return codeView.Guid;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[PdbPatcher] Error reading GUID from DLL {Dll}", dllPath);
            return null;
        }
    }

    /// <summary>
    /// Reads the PDB ID (GUID) from a Portable PDB file.
    /// </summary>
    /// <param name="pdbPath">Path to the PDB file.</param>
    /// <returns>The GUID, or null if not found.</returns>
    public Guid? ReadGuidFromPdb(string pdbPath)
    {
        try
        {
            using var stream = File.OpenRead(pdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var reader = provider.GetMetadataReader();
            
            var id = reader.DebugMetadataHeader?.Id;
            if (id == null || id.Value.IsEmpty)
                return null;

            // The ID is 20 bytes: 16-byte GUID + 4-byte stamp
            var idBytes = id.Value.ToArray();
            if (idBytes.Length >= 16)
            {
                return new Guid(idBytes.Take(16).ToArray());
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[PdbPatcher] Error reading GUID from PDB {Pdb}", pdbPath);
            return null;
        }
    }

    /// <summary>
    /// Patches a Portable PDB file to use a new GUID.
    /// </summary>
    /// <param name="pdbPath">Path to the PDB file.</param>
    /// <param name="newGuid">The new GUID to write.</param>
    /// <returns>True if successful, false otherwise.</returns>
    private bool PatchPdbGuid(string pdbPath, Guid newGuid)
    {
        try
        {
            // Read the entire PDB file
            var pdbBytes = File.ReadAllBytes(pdbPath);

            // Find the PDB ID location
            // In a Portable PDB, the ID is stored in the #Pdb stream
            // The structure is: metadata signature, then streams
            // We need to find the offset of the PDB ID
            
            var offset = FindPdbIdOffset(pdbBytes);
            if (offset < 0)
            {
                _logger?.LogWarning("[PdbPatcher] Could not find PDB ID offset in {Pdb}", pdbPath);
                return false;
            }

            // Write the new GUID at the found offset
            var guidBytes = newGuid.ToByteArray();
            Array.Copy(guidBytes, 0, pdbBytes, offset, 16);

            // Write the patched file back
            File.WriteAllBytes(pdbPath, pdbBytes);

            _logger?.LogDebug("[PdbPatcher] Successfully patched PDB at offset {Offset}", offset);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PdbPatcher] Error patching PDB file {Pdb}", pdbPath);
            return false;
        }
    }

    /// <summary>
    /// Finds the offset of the PDB ID in a Portable PDB file.
    /// </summary>
    /// <param name="pdbBytes">The PDB file bytes.</param>
    /// <returns>The offset, or -1 if not found.</returns>
    private int FindPdbIdOffset(byte[] pdbBytes)
    {
        // Portable PDB structure:
        // - Metadata root: starts with signature 0x424A5342 ("BSJB")
        // - After the metadata root header, there are stream headers
        // - One of the streams is #Pdb which contains the PDB ID at offset 0
        
        // Look for "BSJB" signature
        int metadataStart = -1;
        for (int i = 0; i < pdbBytes.Length - 4; i++)
        {
            if (pdbBytes[i] == 0x42 && pdbBytes[i + 1] == 0x53 &&
                pdbBytes[i + 2] == 0x4A && pdbBytes[i + 3] == 0x42) // "BSJB"
            {
                metadataStart = i;
                break;
            }
        }

        if (metadataStart < 0)
        {
            _logger?.LogDebug("[PdbPatcher] BSJB signature not found");
            return -1;
        }

        // Parse the metadata root to find the #Pdb stream
        // Metadata root structure:
        // - 4 bytes: signature ("BSJB")
        // - 2 bytes: major version
        // - 2 bytes: minor version
        // - 4 bytes: reserved
        // - 4 bytes: length of version string
        // - N bytes: version string (padded to 4-byte boundary)
        // - 2 bytes: flags
        // - 2 bytes: number of streams
        // - Stream headers...

        int offset = metadataStart + 4; // Skip signature
        offset += 2 + 2 + 4; // Skip versions and reserved

        if (offset + 4 > pdbBytes.Length) return -1;

        int versionLength = BitConverter.ToInt32(pdbBytes, offset);
        offset += 4;

        // Skip version string (padded to 4-byte boundary)
        int paddedVersionLength = (versionLength + 3) & ~3;
        offset += paddedVersionLength;

        if (offset + 4 > pdbBytes.Length) return -1;

        // Skip flags
        offset += 2;

        // Read number of streams
        int streamCount = BitConverter.ToInt16(pdbBytes, offset);
        offset += 2;

        // Parse stream headers to find #Pdb
        for (int i = 0; i < streamCount; i++)
        {
            if (offset + 8 > pdbBytes.Length) return -1;

            int streamOffset = BitConverter.ToInt32(pdbBytes, offset);
            int streamSize = BitConverter.ToInt32(pdbBytes, offset + 4);
            offset += 8;

            // Read stream name (null-terminated, padded to 4-byte boundary)
            int nameStart = offset;
            while (offset < pdbBytes.Length && pdbBytes[offset] != 0)
                offset++;
            
            string streamName = System.Text.Encoding.ASCII.GetString(pdbBytes, nameStart, offset - nameStart);
            
            // Skip null terminator and padding
            offset++;
            offset = (offset + 3) & ~3;

            if (streamName == "#Pdb")
            {
                // The #Pdb stream starts with the 20-byte PDB ID
                // Return the absolute offset in the file
                return metadataStart + streamOffset;
            }
        }

        _logger?.LogDebug("[PdbPatcher] #Pdb stream not found");
        return -1;
    }

    /// <summary>
    /// Patches all PDB files in a directory to match their corresponding DLLs.
    /// </summary>
    /// <param name="symbolsDirectory">Directory containing DLLs and PDBs.</param>
    /// <returns>List of patch results.</returns>
    public List<PatchResult> PatchAllPdbsInDirectory(string symbolsDirectory)
    {
        var results = new List<PatchResult>();

        if (!Directory.Exists(symbolsDirectory))
        {
            _logger?.LogWarning("[PdbPatcher] Directory does not exist: {Dir}", symbolsDirectory);
            return results;
        }

        // Find all PDB files
        var pdbFiles = Directory.GetFiles(symbolsDirectory, "*.pdb", SearchOption.AllDirectories);
        _logger?.LogInformation("[PdbPatcher] Found {Count} PDB files in {Dir}", pdbFiles.Length, symbolsDirectory);

        foreach (var pdbPath in pdbFiles)
        {
            // Find the corresponding DLL
            var pdbDir = Path.GetDirectoryName(pdbPath) ?? symbolsDirectory;
            var pdbName = Path.GetFileNameWithoutExtension(pdbPath);
            var dllPath = Path.Combine(pdbDir, pdbName + ".dll");

            if (!File.Exists(dllPath))
            {
                _logger?.LogDebug("[PdbPatcher] No matching DLL for {Pdb}", Path.GetFileName(pdbPath));
                continue;
            }

            var result = PatchPdbToMatchDll(dllPath, pdbPath);
            results.Add(result);
        }

        return results;
    }
    
    /// <summary>
    /// Patches PDB files to match the GUIDs of modules loaded in the dump.
    /// This is used when the downloaded PDBs are from a different build than the DLLs in the dump.
    /// </summary>
    /// <param name="symbolsDirectory">Directory containing downloaded PDBs.</param>
    /// <param name="moduleGuids">Dictionary mapping module name (without extension) to expected GUID.</param>
    /// <returns>List of patch results.</returns>
    public List<PatchResult> PatchPdbsToMatchModuleGuids(string symbolsDirectory, Dictionary<string, Guid> moduleGuids)
    {
        var results = new List<PatchResult>();

        if (!Directory.Exists(symbolsDirectory))
        {
            _logger?.LogWarning("[PdbPatcher] Directory does not exist: {Dir}", symbolsDirectory);
            return results;
        }

        // Find all PDB files
        var pdbFiles = Directory.GetFiles(symbolsDirectory, "*.pdb", SearchOption.AllDirectories);
        _logger?.LogInformation("[PdbPatcher] Found {Count} PDB files, checking against {ModuleCount} module GUIDs", 
            pdbFiles.Length, moduleGuids.Count);

        foreach (var pdbPath in pdbFiles)
        {
            var pdbName = Path.GetFileNameWithoutExtension(pdbPath);
            
            // Check if we have a target GUID for this module
            if (!moduleGuids.TryGetValue(pdbName, out var expectedGuid))
            {
                _logger?.LogDebug("[PdbPatcher] No target GUID for {Pdb}", pdbName);
                continue;
            }

            var result = new PatchResult
            {
                PdbPath = pdbPath,
                DllPath = $"[module in dump: {pdbName}]",
                NewGuid = expectedGuid
            };

            try
            {
                // Read the current GUID from the PDB
                var pdbGuid = ReadGuidFromPdb(pdbPath);
                if (pdbGuid == null)
                {
                    result.ErrorMessage = "Could not read GUID from PDB";
                    _logger?.LogWarning("[PdbPatcher] {Error}: {Pdb}", result.ErrorMessage, pdbPath);
                    results.Add(result);
                    continue;
                }
                result.OriginalGuid = pdbGuid.Value;

                // Check if patching is needed
                if (expectedGuid == pdbGuid.Value)
                {
                    _logger?.LogDebug("[PdbPatcher] GUIDs already match for {Pdb}", Path.GetFileName(pdbPath));
                    result.Success = true;
                    result.WasPatched = false;
                    results.Add(result);
                    continue;
                }

                // Patch the PDB
                _logger?.LogInformation("[PdbPatcher] Patching {Pdb}: {OldGuid} -> {NewGuid}",
                    Path.GetFileName(pdbPath), pdbGuid.Value, expectedGuid);

                var patched = PatchPdbGuid(pdbPath, expectedGuid);
                if (patched)
                {
                    result.WasPatched = true;
                    
                    // Verify the patch by re-reading the PDB
                    var verifiedGuid = ReadGuidFromPdb(pdbPath);
                    result.VerifiedGuid = verifiedGuid;
                    
                    if (verifiedGuid == expectedGuid)
                    {
                        result.Success = true;
                        result.Verified = true;
                        _logger?.LogInformation("[PdbPatcher] ✓ Verified {Pdb}: GUID now {Guid}",
                            Path.GetFileName(pdbPath), verifiedGuid);
                    }
                    else
                    {
                        result.Success = false;
                        result.Verified = false;
                        result.ErrorMessage = $"Verification failed: expected {expectedGuid}, got {verifiedGuid}";
                        _logger?.LogError("[PdbPatcher] ✗ Verification failed for {Pdb}: expected {Expected}, got {Actual}",
                            Path.GetFileName(pdbPath), expectedGuid, verifiedGuid);
                    }
                }
                else
                {
                    result.ErrorMessage = "Failed to patch PDB file";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                _logger?.LogError(ex, "[PdbPatcher] Error patching {Pdb}", pdbPath);
            }

            results.Add(result);
        }

        return results;
    }
}

