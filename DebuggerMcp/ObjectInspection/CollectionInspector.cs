using System.Text.RegularExpressions;
using DebuggerMcp.Analysis;
using DebuggerMcp.ObjectInspection.Models;
using Microsoft.Extensions.Logging;

namespace DebuggerMcp.ObjectInspection;

/// <summary>
/// Provides specialized inspection for .NET collection types.
/// </summary>
public partial class CollectionInspector
{
    /// <summary>
    /// Default maximum number of collection elements to extract.
    /// </summary>
    public const int DefaultMaxCollectionElements = 10;

    private readonly ILogger _logger;
    private readonly ObjectInspector _objectInspector;

    public CollectionInspector(ILogger logger, ObjectInspector objectInspector)
    {
        _logger = logger;
        _objectInspector = objectInspector;
    }

    /// <summary>
    /// Result of collection extraction, including partial results on error.
    /// </summary>
    public class ExtractionResult
    {
        public List<object?>? Elements { get; set; }
        public List<CollectionEntry>? Entries { get; set; }
        public int Count { get; set; }
        public int Capacity { get; set; }
        public int ElementsReturned { get; set; }
        public bool Truncated { get; set; }
        public string? Error { get; set; }
    }


    /// <summary>
    /// Extracts elements from a List&lt;T&gt; collection.
    /// Returns partial results with error on failure.
    /// </summary>
    public async Task<ExtractionResult> ExtractListElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // Step 1: Find _items field
            var itemsField = fields.FirstOrDefault(f => f.Name == "_items");
            if (itemsField == null)
            {
                result.Error = "_items field not found";
                return result;
            }

            // Step 2: Find _size field
            var sizeField = fields.FirstOrDefault(f => f.Name == "_size");
            if (sizeField == null)
            {
                result.Error = "_size field not found";
                return result;
            }

            // Step 3: Parse count
            if (!int.TryParse(sizeField.Value, out var count))
            {
                result.Error = $"Could not parse _size value: {sizeField.Value}";
                return result;
            }

            result.Count = count;

            // Step 4: Get items array address
            var itemsAddress = itemsField.Value;
            if (string.IsNullOrEmpty(itemsAddress) || PrimitiveResolver.IsNullAddress(itemsAddress))
            {
                // Empty list - return success with zero elements instead of erroring
                result.ElementsReturned = 0;
                return result;
            }

            // Step 5: Calculate elements to fetch
            var actualCount = Math.Min(count, maxElements);
            result.Truncated = count > maxElements;

            if (actualCount == 0)
            {
                // Nothing to fetch; keep result empty
                result.ElementsReturned = 0;
                return result;
            }

            // Step 6: Execute dumparray
            var normalizedAddress = PrimitiveResolver.NormalizeAddress(itemsAddress);
            var output = manager.ExecuteCommand($"dumparray -length {actualCount} {normalizedAddress}");

            // Check for errors
            if (output.Contains("Invalid") && output.Contains("address", StringComparison.OrdinalIgnoreCase))
            {
                result.Error = $"dumparray failed: {output.Split('\n').FirstOrDefault()}";
                return result;
            }

            // Get capacity from array length
            var capacityMatch = Regex.Match(output, @"Number of elements (\d+)");
            result.Capacity = capacityMatch.Success ? int.Parse(capacityMatch.Groups[1].Value) : count;

            // Get element type for optimization
            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            // Step 7 & 8: Parse elements and resolve
            await ParseAndResolveArrayElementsAsync(
                manager,
                output,
                elementType,
                isInlineable,
                actualCount,
                depth,
                maxDepth,
                maxElements,
                maxStringLength,
                seenAddresses,
                rootAddress,
                result,
                cancellationToken);

            result.ElementsReturned = result.Elements!.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"List extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "List extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts elements from a Stack&lt;T&gt; collection.
    /// </summary>
    public async Task<ExtractionResult> ExtractStackElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // Stack uses _array and _size (same structure as List but different field name)
            var arrayField = fields.FirstOrDefault(f => f.Name == "_array");
            var sizeField = fields.FirstOrDefault(f => f.Name == "_size");

            if (arrayField == null || sizeField == null)
            {
                result.Error = "_array or _size field not found";
                return result;
            }

            if (!int.TryParse(sizeField.Value, out var count))
            {
                result.Error = $"Could not parse _size value: {sizeField.Value}";
                return result;
            }

            result.Count = count;

            if (count == 0 || PrimitiveResolver.IsNullAddress(arrayField.Value))
            {
                result.ElementsReturned = 0;
                return result;
            }

            var actualCount = Math.Min(count, maxElements);
            result.Truncated = count > maxElements;

            var normalizedAddress = PrimitiveResolver.NormalizeAddress(arrayField.Value);
            var output = manager.ExecuteCommand($"dumparray -length {count} {normalizedAddress}");

            var capacityMatch = Regex.Match(output, @"Number of elements (\d+)");
            result.Capacity = capacityMatch.Success ? int.Parse(capacityMatch.Groups[1].Value) : count;

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            // Parse all elements first
            var allElements = ParseArrayElementsByIndex(output);

            // Stack elements are in reverse order (LIFO) - top of stack is at index _size-1
            for (int i = count - 1; i >= 0 && result.Elements!.Count < actualCount; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (allElements.TryGetValue(i, out var elementValue))
                {
                    var resolved = await ResolveElementAsync(
                        manager, elementValue, elementType, isInlineable,
                        depth, maxDepth, maxElements, maxStringLength,
                        seenAddresses, rootAddress, i, result, cancellationToken);
                    result.Elements.Add(resolved);
                }
            }

            result.ElementsReturned = result.Elements!.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"Stack extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "Stack extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts elements from a Queue&lt;T&gt; collection (circular buffer).
    /// </summary>
    public async Task<ExtractionResult> ExtractQueueElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // Queue uses circular buffer with _array, _head, _tail, _size
            var arrayField = fields.FirstOrDefault(f => f.Name == "_array");
            var headField = fields.FirstOrDefault(f => f.Name == "_head");
            var sizeField = fields.FirstOrDefault(f => f.Name == "_size");

            if (arrayField == null || headField == null || sizeField == null)
            {
                result.Error = "_array, _head, or _size field not found";
                return result;
            }

            if (!int.TryParse(sizeField.Value, out var size) ||
                !int.TryParse(headField.Value, out var head))
            {
                result.Error = $"Could not parse _size or _head values";
                return result;
            }

            result.Count = size;

            if (size == 0 || PrimitiveResolver.IsNullAddress(arrayField.Value))
            {
                result.ElementsReturned = 0;
                return result;
            }

            var actualCount = Math.Min(size, maxElements);
            result.Truncated = size > maxElements;

            // Dump the entire array
            var normalizedAddress = PrimitiveResolver.NormalizeAddress(arrayField.Value);
            var output = manager.ExecuteCommand($"dumparray {normalizedAddress}");

            // Parse array length
            var lengthMatch = Regex.Match(output, @"Number of elements (\d+)");
            if (!lengthMatch.Success)
            {
                result.Error = "Could not determine array length";
                return result;
            }
            var arrayLength = int.Parse(lengthMatch.Groups[1].Value);
            result.Capacity = arrayLength;

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            // Parse all elements into a dictionary by index
            var allElements = ParseArrayElementsByIndex(output);

            // Extract in queue order (circular)
            for (int i = 0; i < actualCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var actualIndex = (head + i) % arrayLength;

                if (allElements.TryGetValue(actualIndex, out var elementValue))
                {
                    var resolved = await ResolveElementAsync(
                        manager, elementValue, elementType, isInlineable,
                        depth, maxDepth, maxElements, maxStringLength,
                        seenAddresses, rootAddress, actualIndex, result, cancellationToken);
                    result.Elements!.Add(resolved);
                }
                else
                {
                    result.Elements!.Add(null);
                }
            }

            result.ElementsReturned = result.Elements!.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"Queue extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "Queue extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts elements from a HashSet&lt;T&gt; collection.
    /// </summary>
    public async Task<ExtractionResult> ExtractHashSetElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // Find _entries and _count
            var entriesField = fields.FirstOrDefault(f => f.Name == "_entries");
            var countField = fields.FirstOrDefault(f => f.Name == "_count");

            if (entriesField == null || countField == null)
            {
                result.Error = "_entries or _count field not found";
                return result;
            }

            if (!int.TryParse(countField.Value, out var count))
            {
                result.Error = $"Could not parse _count: {countField.Value}";
                return result;
            }

            result.Count = count;
            result.Truncated = count > maxElements;

            if (count == 0 || PrimitiveResolver.IsNullAddress(entriesField.Value))
            {
                result.ElementsReturned = 0;
                return result;
            }

            var entriesAddress = PrimitiveResolver.NormalizeAddress(entriesField.Value);

            // Dump array to get base info and element MT
            var output = manager.ExecuteCommand($"dumparray {entriesAddress}");
            var arrayLengthMatch = Regex.Match(output, @"Number of elements (\d+)");
            var arrayLength = arrayLengthMatch.Success ? int.Parse(arrayLengthMatch.Groups[1].Value) : count;
            result.Capacity = arrayLength;

            // Try to get Entry MethodTable from dumparray output first
            var entryMt = ExtractMethodTableFromDumpArray(output);

            // Fallback: try CollectionTypeDetector method
            if (string.IsNullOrEmpty(entryMt))
            {
                entryMt = CollectionTypeDetector.GetArrayElementMethodTable(manager, entriesAddress);
            }

            if (string.IsNullOrEmpty(entryMt))
            {
                result.Error = "Could not determine Entry MethodTable";
                return result;
            }

            // Get Entry size from dumparray output or dumpmt
            var entrySize = ExtractElementSizeFromDumpArray(output);
            if (entrySize <= 0)
            {
                entrySize = CollectionTypeDetector.GetTypeSize(manager, entryMt);
            }

            if (entrySize <= 0)
            {
                result.Error = "Could not determine Entry size";
                return result;
            }

            // Get element type for optimization
            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var pointerSize = CollectionTypeDetector.GetPointerSize(manager);

            // Iterate through entries (value type array - calculate addresses)
            // Iterate through entries - scan the array looking for valid entries
            // HashSet Entry struct has: hashCode, next, value
            // Free slots have next < -1 (pointing to free list)
            var extractedCount = 0;
            var validEntriesFound = 0;
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            for (int i = 0; i < arrayLength && extractedCount < maxElements && validEntriesFound < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Calculate Entry address
                    var entryAddr = CollectionTypeDetector.CalculateArrayElementAddress(entriesAddress, i, entrySize, pointerSize);

                    // Dump the Entry value type
                    var entryOutput = manager.ExecuteCommand($"dumpvc {entryMt} {entryAddr}");
                    var entryFields = DumpVcParser.Parse(entryOutput);

                    if (!entryFields.Success)
                        continue;

                    // Check if this is a free slot by examining the 'next' field
                    // Free entries have next < -1 (pointing to free list)
                    var nextField = entryFields.Fields.FirstOrDefault(f => f.Name is "Next" or "_next" or "next");
                    if (nextField != null && int.TryParse(nextField.Value, out var nextValue) && nextValue < -1)
                    {
                        continue; // Free slot
                    }

                    // Extract Value field
                    var valueField = entryFields.Fields.FirstOrDefault(f =>
                        f.Name is "Value" or "_value" or "value");
                    if (valueField == null)
                        continue;

                    // For reference types, skip null values (indicates empty slot)
                    if (!isInlineable && PrimitiveResolver.IsNullAddress(valueField.Value))
                        continue;

                    // This is a valid entry
                    validEntriesFound++;

                    // Resolve the value
                    var resolved = await ResolveElementAsync(
                        manager, valueField.Value, elementType, isInlineable,
                        depth, maxDepth, maxElements, maxStringLength,
                        seenAddresses, rootAddress, i, result, cancellationToken);

                    result.Elements!.Add(resolved);
                    extractedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to extract HashSet entry at index {Index}", i);
                    if (result.Error == null)
                        result.Error = $"Failed at entry {i}: {ex.Message}";
                }
            }

            result.ElementsReturned = result.Elements!.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"HashSet extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "HashSet extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts entries from a Dictionary&lt;K,V&gt; collection.
    /// </summary>
    public async Task<ExtractionResult> ExtractDictionaryEntriesAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Entries = new List<CollectionEntry>() };

        try
        {
            // Find _entries and _count
            var entriesField = fields.FirstOrDefault(f => f.Name == "_entries");
            var countField = fields.FirstOrDefault(f => f.Name == "_count");

            if (entriesField == null || countField == null)
            {
                result.Error = "_entries or _count field not found";
                return result;
            }

            if (!int.TryParse(countField.Value, out var count))
            {
                result.Error = $"Could not parse _count: {countField.Value}";
                return result;
            }

            result.Count = count;
            result.Truncated = count > maxElements;

            if (count == 0 || PrimitiveResolver.IsNullAddress(entriesField.Value))
            {
                result.ElementsReturned = 0;
                return result;
            }

            var entriesAddress = PrimitiveResolver.NormalizeAddress(entriesField.Value);

            // Dump array to get base info and element MT
            var output = manager.ExecuteCommand($"dumparray {entriesAddress}");
            var arrayLengthMatch = Regex.Match(output, @"Number of elements (\d+)");
            var arrayLength = arrayLengthMatch.Success ? int.Parse(arrayLengthMatch.Groups[1].Value) : count;
            result.Capacity = arrayLength;

            // Try to get Entry MethodTable from dumparray output first
            // dumparray for value type arrays shows: "Name:        System.Collections.Generic.Dictionary`2+Entry[[...]]"
            // and "MethodTable: 00007ff..."
            var entryMt = ExtractMethodTableFromDumpArray(output);

            // Fallback: try CollectionTypeDetector method
            if (string.IsNullOrEmpty(entryMt))
            {
                entryMt = CollectionTypeDetector.GetArrayElementMethodTable(manager, entriesAddress);
            }

            if (string.IsNullOrEmpty(entryMt))
            {
                result.Error = "Could not determine Entry MethodTable";
                return result;
            }

            // Get Entry size from dumparray output or dumpmt
            var entrySize = ExtractElementSizeFromDumpArray(output);
            if (entrySize <= 0)
            {
                entrySize = CollectionTypeDetector.GetTypeSize(manager, entryMt);
            }

            if (entrySize <= 0)
            {
                result.Error = "Could not determine Entry size";
                return result;
            }

            // Get key/value types for optimization
            var kvTypes = CollectionTypeDetector.ExtractKeyValueTypes(collectionTypeName);
            var keyInlineable = kvTypes?.KeyType != null && CollectionTypeDetector.IsInlineableType(kvTypes.Value.KeyType);
            var valueInlineable = kvTypes?.ValueType != null && CollectionTypeDetector.IsInlineableType(kvTypes.Value.ValueType);
            var pointerSize = CollectionTypeDetector.GetPointerSize(manager);

            // Iterate through entries - scan the array looking for valid entries
            // Dictionary Entry struct has: hashCode, next, key, value
            // Free slots have next pointing to free list (negative values indicate free)
            var extractedCount = 0;
            var validEntriesFound = 0;

            for (int i = 0; i < arrayLength && extractedCount < maxElements && validEntriesFound < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Calculate Entry address
                    var entryAddr = CollectionTypeDetector.CalculateArrayElementAddress(entriesAddress, i, entrySize, pointerSize);

                    // Dump the Entry value type
                    var entryOutput = manager.ExecuteCommand($"dumpvc {entryMt} {entryAddr}");
                    var entryFields = DumpVcParser.Parse(entryOutput);

                    if (!entryFields.Success)
                        continue;

                    // Check if this is a free slot by examining the 'next' field
                    // In .NET Dictionary, free entries have next < -1 (pointing to free list)
                    // Valid entries have next >= -1 (-1 means end of bucket chain)
                    var nextField = entryFields.Fields.FirstOrDefault(f => f.Name is "next" or "_next");
                    if (nextField != null && int.TryParse(nextField.Value, out var nextValue) && nextValue < -1)
                    {
                        // This is a free slot, skip it
                        continue;
                    }

                    // Extract key and value fields
                    var keyField = entryFields.Fields.FirstOrDefault(f =>
                        f.Name is "key" or "_key" or "Key");
                    var valueField = entryFields.Fields.FirstOrDefault(f =>
                        f.Name is "value" or "_value" or "Value");

                    if (keyField == null && valueField == null)
                        continue;

                    // For reference type keys, null key with non-negative next is still a free slot
                    // (can happen when key is cleared but slot not yet recycled)
                    if (keyField != null && PrimitiveResolver.IsNullAddress(keyField.Value) && !keyInlineable)
                    {
                        continue; // Skip null key entries for reference type keys
                    }

                    // This is a valid entry
                    validEntriesFound++;

                    // Resolve key and value
                    var resolvedKey = keyField != null
                        ? await ResolveElementAsync(
                            manager, keyField.Value, kvTypes?.KeyType, keyInlineable,
                            depth, maxDepth, maxElements, maxStringLength,
                            seenAddresses, rootAddress, i, result, cancellationToken)
                        : null;

                    var resolvedValue = valueField != null
                        ? await ResolveElementAsync(
                            manager, valueField.Value, kvTypes?.ValueType, valueInlineable,
                            depth, maxDepth, maxElements, maxStringLength,
                            seenAddresses, rootAddress, i, result, cancellationToken)
                        : null;

                    // Only add if we have at least a key (value can be null for Dictionary<K,V> where V is reference type)
                    if (resolvedKey != null || keyInlineable)
                    {
                        result.Entries!.Add(new CollectionEntry
                        {
                            Key = resolvedKey,
                            Value = resolvedValue
                        });
                        extractedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to extract Dictionary entry at index {Index}", i);
                    if (result.Error == null)
                        result.Error = $"Failed at entry {i}: {ex.Message}";
                }
            }

            result.ElementsReturned = result.Entries!.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Entries?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"Dictionary extraction failed: {ex.Message}";
            result.ElementsReturned = result.Entries?.Count ?? 0;
            _logger.LogError(ex, "Dictionary extraction failed");
        }

        return result;
    }

    /// <summary>
    /// Extracts Element MethodTable from dumparray output for value type arrays.
    /// Note: dumparray shows "MethodTable:" (array's MT) and "Element Methodtable:" (element's MT).
    /// We need the Element MethodTable for dumpvc.
    /// </summary>
    private static string? ExtractMethodTableFromDumpArray(string dumparrayOutput)
    {
        // First try: "Element Methodtable:" or "Element Type:" (what we need for value type arrays)
        var elementMtMatch = Regex.Match(dumparrayOutput,
            @"Element\s+(?:Methodtable|Type):\s+([0-9a-fA-Fx]+)",
            RegexOptions.IgnoreCase);
        if (elementMtMatch.Success)
        {
            return elementMtMatch.Groups[1].Value;
        }

        // Fallback: "ComponentMethodTable:" (alternative naming)
        var componentMtMatch = Regex.Match(dumparrayOutput,
            @"ComponentMethodTable:\s+([0-9a-fA-Fx]+)",
            RegexOptions.IgnoreCase);
        if (componentMtMatch.Success)
        {
            return componentMtMatch.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Extracts element size from dumparray output.
    /// </summary>
    private static int ExtractElementSizeFromDumpArray(string dumparrayOutput)
    {
        // Try "Element Size:" first (most common for value type arrays)
        var elementSizeMatch = Regex.Match(dumparrayOutput, @"Element\s+Size:\s+(\d+)", RegexOptions.IgnoreCase);
        if (elementSizeMatch.Success && int.TryParse(elementSizeMatch.Groups[1].Value, out var elementSize))
        {
            return elementSize;
        }

        // Fallback: "ComponentSize:" in hex
        var componentSizeMatch = Regex.Match(dumparrayOutput, @"ComponentSize:\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (componentSizeMatch.Success && int.TryParse(componentSizeMatch.Groups[1].Value,
            System.Globalization.NumberStyles.HexNumber, null, out var compSize))
        {
            return compSize;
        }

        return -1;
    }



    /// <summary>
    /// Extracts entries from a ConcurrentDictionary&lt;K,V&gt; collection.
    /// </summary>
    public async Task<ExtractionResult> ExtractConcurrentDictionaryEntriesAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Entries = new List<CollectionEntry>() };

        try
        {
            // Find _tables field
            var tablesField = fields.FirstOrDefault(f => f.Name == "_tables");
            if (tablesField == null || PrimitiveResolver.IsNullAddress(tablesField.Value))
            {
                result.Error = "_tables field not found or null";
                return result;
            }

            // Inspect _tables object
            var tablesAddr = PrimitiveResolver.NormalizeAddress(tablesField.Value);
            var tablesOutput = manager.ExecuteCommand($"dumpobj {tablesAddr}");
            var tablesResult = DumpObjParser.Parse(tablesOutput);

            if (!tablesResult.Success)
            {
                result.Error = "Could not parse _tables object";
                return result;
            }

            // Get accurate count from _countPerLock array
            var countPerLockField = tablesResult.Fields.FirstOrDefault(f => f.Name == "_countPerLock");
            result.Count = await GetConcurrentDictionaryCountAsync(manager, countPerLockField?.Value, cancellationToken);
            result.Truncated = result.Count > maxElements;

            // Find _buckets field in Tables
            var bucketsField = tablesResult.Fields.FirstOrDefault(f => f.Name == "_buckets");
            if (bucketsField == null || PrimitiveResolver.IsNullAddress(bucketsField.Value))
            {
                result.ElementsReturned = 0;
                return result;
            }

            // Dump buckets array
            var bucketsAddr = PrimitiveResolver.NormalizeAddress(bucketsField.Value);
            var bucketsOutput = manager.ExecuteCommand($"dumparray {bucketsAddr}");

            var bucketAddresses = ParseArrayElements(bucketsOutput);
            result.Capacity = bucketAddresses.Count;

            // Get key/value types for optimization
            var kvTypes = CollectionTypeDetector.ExtractKeyValueTypes(collectionTypeName);

            foreach (var bucketAddr in bucketAddresses)
            {
                if (result.Entries!.Count >= maxElements)
                    break;

                cancellationToken.ThrowIfCancellationRequested();

                // Skip null buckets
                if (PrimitiveResolver.IsNullAddress(bucketAddr))
                    continue;

                // Walk the linked list starting from this Node
                await WalkNodeChainAsync(
                    manager, bucketAddr, kvTypes,
                    result, maxElements,
                    depth, maxDepth, maxElements, maxStringLength,
                    seenAddresses, rootAddress, cancellationToken);
            }

            result.ElementsReturned = result.Entries!.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Entries?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ConcurrentDictionary extraction failed: {ex.Message}";
            result.ElementsReturned = result.Entries?.Count ?? 0;
            _logger.LogError(ex, "ConcurrentDictionary extraction failed");
        }

        return result;
    }

    private async Task<int> GetConcurrentDictionaryCountAsync(IDebuggerManager manager, string? countPerLockAddr, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(countPerLockAddr) || PrimitiveResolver.IsNullAddress(countPerLockAddr))
            return 0;

        try
        {
            var normalizedAddr = PrimitiveResolver.NormalizeAddress(countPerLockAddr);
            var output = manager.ExecuteCommand($"dumparray {normalizedAddr}");

            // Sum all the counts
            var countPattern = CountElementRegex();
            var totalCount = 0;

            foreach (Match match in countPattern.Matches(output))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (int.TryParse(match.Groups[1].Value, out var count))
                    totalCount += count;
            }

            return totalCount;
        }
        catch
        {
            return 0;
        }
    }

    [GeneratedRegex(@"\[\d+\]\s+(\d+)")]
    private static partial Regex CountElementRegex();

    private async Task WalkNodeChainAsync(
        IDebuggerManager manager,
        string nodeAddr,
        (string KeyType, string ValueType)? kvTypes,
        ExtractionResult result,
        int maxElements,
        int depth,
        int maxDepth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var currentAddr = nodeAddr;
        var visited = new HashSet<string>(); // Prevent infinite loops

        var keyInlineable = kvTypes?.KeyType != null && CollectionTypeDetector.IsInlineableType(kvTypes.Value.KeyType);
        var valueInlineable = kvTypes?.ValueType != null && CollectionTypeDetector.IsInlineableType(kvTypes.Value.ValueType);

        while (!string.IsNullOrEmpty(currentAddr) &&
               !PrimitiveResolver.IsNullAddress(currentAddr) &&
               result.Entries!.Count < maxElements &&
               !visited.Contains(currentAddr))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited.Add(currentAddr);

            try
            {
                // Dump current Node
                var normalizedAddr = PrimitiveResolver.NormalizeAddress(currentAddr);
                var nodeOutput = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
                var nodeResult = DumpObjParser.Parse(nodeOutput);

                if (!nodeResult.Success)
                    break;

                // Extract _key and _value
                var keyField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_key");
                var valueField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_value");

                if (keyField != null && valueField != null)
                {
                    var resolvedKey = await ResolveElementAsync(
                        manager, keyField.Value, kvTypes?.KeyType, keyInlineable,
                        depth, maxDepth, maxArrayElements, maxStringLength,
                        seenAddresses, rootAddress, 0, result, cancellationToken);

                    var resolvedValue = await ResolveElementAsync(
                        manager, valueField.Value, kvTypes?.ValueType, valueInlineable,
                        depth, maxDepth, maxArrayElements, maxStringLength,
                        seenAddresses, rootAddress, 0, result, cancellationToken);

                    result.Entries!.Add(new CollectionEntry { Key = resolvedKey, Value = resolvedValue });
                }

                // Move to next node in chain
                var nextField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_next");
                currentAddr = nextField?.Value ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to process node at {Address}", currentAddr);
                if (result.Error == null)
                    result.Error = $"Failed at node {currentAddr}: {ex.Message}";
                break;
            }
        }
    }



    /// <summary>
    /// Extracts elements from an ImmutableArray&lt;T&gt;.
    /// </summary>
    public async Task<ExtractionResult> ExtractImmutableArrayElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // ImmutableArray is a struct with a single 'array' field
            var arrayField = fields.FirstOrDefault(f => f.Name == "array");

            if (arrayField == null || PrimitiveResolver.IsNullAddress(arrayField.Value))
            {
                result.ElementsReturned = 0;
                result.Count = 0;
                return result;
            }

            // Use standard array extraction
            var normalizedAddress = PrimitiveResolver.NormalizeAddress(arrayField.Value);
            var output = manager.ExecuteCommand($"dumparray {normalizedAddress}");

            // Get actual count from array
            var lengthMatch = Regex.Match(output, @"Number of elements (\d+)");
            var count = lengthMatch.Success ? int.Parse(lengthMatch.Groups[1].Value) : 0;
            result.Count = count;
            result.Capacity = count;

            var actualCount = Math.Min(count, maxElements);
            result.Truncated = count > maxElements;

            if (count == 0)
            {
                result.ElementsReturned = 0;
                return result;
            }

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            await ParseAndResolveArrayElementsAsync(
                manager,
                output,
                elementType,
                isInlineable,
                actualCount,
                depth,
                maxDepth,
                maxElements,
                maxStringLength,
                seenAddresses,
                rootAddress,
                result,
                cancellationToken);

            result.ElementsReturned = result.Elements!.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ImmutableArray extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "ImmutableArray extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts elements from an ImmutableList&lt;T&gt; using in-order tree traversal.
    /// </summary>
    public async Task<ExtractionResult> ExtractImmutableListElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // Find _root field
            var rootField = fields.FirstOrDefault(f => f.Name == "_root");

            if (rootField == null || PrimitiveResolver.IsNullAddress(rootField.Value))
            {
                result.ElementsReturned = 0;
                result.Count = 0;
                return result;
            }

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            var visited = new HashSet<string>();

            await InOrderTraverseAsync(
                manager, rootField.Value, elementType, isInlineable, result.Elements!, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);

            result.Count = result.Elements!.Count;
            result.ElementsReturned = result.Elements.Count;
            // ImmutableList doesn't have a separate count field easily accessible,
            // so we report what we found (may be truncated)
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ImmutableList extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "ImmutableList extraction failed");
        }

        return result;
    }

    private async Task InOrderTraverseAsync(
        IDebuggerManager manager,
        string nodeAddr,
        string? elementType,
        bool isInlineable,
        List<object?> elements,
        int maxElements,
        HashSet<string> visited,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        ExtractionResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(nodeAddr) ||
            PrimitiveResolver.IsNullAddress(nodeAddr) ||
            elements.Count >= maxElements ||
            visited.Contains(nodeAddr))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        visited.Add(nodeAddr);

        // Dump node
        var normalizedAddr = PrimitiveResolver.NormalizeAddress(nodeAddr);
        var nodeOutput = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
        var nodeResult = DumpObjParser.Parse(nodeOutput);

        if (!nodeResult.Success)
            return;

        // Traverse left subtree first
        var leftField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_left");
        if (leftField != null && !PrimitiveResolver.IsNullAddress(leftField.Value))
        {
            await InOrderTraverseAsync(
                manager, leftField.Value, elementType, isInlineable, elements, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);
        }

        // Add current node's key
        if (elements.Count < maxElements)
        {
            var keyField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_key");
            if (keyField != null)
            {
                var resolved = await ResolveElementAsync(
                    manager, keyField.Value, elementType, isInlineable,
                    depth, maxDepth, maxElements, maxStringLength,
                    seenAddresses, rootAddress, 0, result, cancellationToken);
                elements.Add(resolved);
            }
        }

        // Traverse right subtree
        var rightField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_right");
        if (rightField != null && !PrimitiveResolver.IsNullAddress(rightField.Value))
        {
            await InOrderTraverseAsync(
                manager, rightField.Value, elementType, isInlineable, elements, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);
        }
    }



    /// <summary>
    /// Extracts elements from a ConcurrentQueue&lt;T&gt;.
    /// ConcurrentQueue uses a linked list of segments, each containing an array.
    /// </summary>
    public async Task<ExtractionResult> ExtractConcurrentQueueElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // ConcurrentQueue has _head and _tail segment pointers
            // Each segment has _slots array and _next pointer
            var headField = fields.FirstOrDefault(f => f.Name == "_head");
            var tailField = fields.FirstOrDefault(f => f.Name == "_tail");

            if (headField == null || PrimitiveResolver.IsNullAddress(headField.Value))
            {
                result.ElementsReturned = 0;
                result.Count = 0;
                return result;
            }

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            var visited = new HashSet<string>();
            var currentSegmentAddr = headField.Value;

            // Walk through segments
            while (!string.IsNullOrEmpty(currentSegmentAddr) &&
                   !PrimitiveResolver.IsNullAddress(currentSegmentAddr) &&
                   result.Elements!.Count < maxElements &&
                   !visited.Contains(currentSegmentAddr))
            {
                cancellationToken.ThrowIfCancellationRequested();
                visited.Add(currentSegmentAddr);

                var normalizedAddr = PrimitiveResolver.NormalizeAddress(currentSegmentAddr);
                var segmentOutput = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
                var segmentResult = DumpObjParser.Parse(segmentOutput);

                if (!segmentResult.Success)
                    break;

                // Get _slots array from segment
                var slotsField = segmentResult.Fields.FirstOrDefault(f => f.Name == "_slots");
                if (slotsField != null && !PrimitiveResolver.IsNullAddress(slotsField.Value))
                {
                    var slotsAddr = PrimitiveResolver.NormalizeAddress(slotsField.Value);
                    var slotsOutput = manager.ExecuteCommand($"dumparray {slotsAddr}");

                    // Each slot is a Slot struct with Item field
                    // For simplicity, parse the array elements
                    var slotAddresses = ParseArrayElements(slotsOutput);

                    foreach (var slotAddr in slotAddresses)
                    {
                        if (result.Elements!.Count >= maxElements)
                            break;

                        if (PrimitiveResolver.IsNullAddress(slotAddr))
                            continue;

                        // Dump the Slot struct to get Item
                        var slotOutput = manager.ExecuteCommand($"dumpobj {slotAddr}");
                        var slotResult = DumpObjParser.Parse(slotOutput);

                        if (!slotResult.Success)
                            continue;

                        var itemField = slotResult.Fields.FirstOrDefault(f => f.Name == "Item");
                        if (itemField != null && !PrimitiveResolver.IsNullAddress(itemField.Value))
                        {
                            var resolved = await ResolveElementAsync(
                                manager, itemField.Value, elementType, isInlineable,
                                depth, maxDepth, maxElements, maxStringLength,
                                seenAddresses, rootAddress, result.Elements.Count, result, cancellationToken);
                            result.Elements.Add(resolved);
                        }
                    }
                }

                // Move to next segment
                var nextField = segmentResult.Fields.FirstOrDefault(f => f.Name == "_nextSegment" || f.Name == "_next");
                currentSegmentAddr = nextField?.Value ?? "";
            }

            result.Count = result.Elements!.Count;
            result.ElementsReturned = result.Elements.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ConcurrentQueue extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "ConcurrentQueue extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts elements from a ConcurrentStack&lt;T&gt;.
    /// ConcurrentStack uses a linked list of nodes with _head pointer.
    /// </summary>
    public async Task<ExtractionResult> ExtractConcurrentStackElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // ConcurrentStack has _head pointing to top Node
            // Each Node has _value and _next
            var headField = fields.FirstOrDefault(f => f.Name == "_head");

            if (headField == null || PrimitiveResolver.IsNullAddress(headField.Value))
            {
                result.ElementsReturned = 0;
                result.Count = 0;
                return result;
            }

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            var visited = new HashSet<string>();
            var currentNodeAddr = headField.Value;

            // Walk through nodes (LIFO order - top of stack first)
            while (!string.IsNullOrEmpty(currentNodeAddr) &&
                   !PrimitiveResolver.IsNullAddress(currentNodeAddr) &&
                   result.Elements!.Count < maxElements &&
                   !visited.Contains(currentNodeAddr))
            {
                cancellationToken.ThrowIfCancellationRequested();
                visited.Add(currentNodeAddr);

                var normalizedAddr = PrimitiveResolver.NormalizeAddress(currentNodeAddr);
                var nodeOutput = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
                var nodeResult = DumpObjParser.Parse(nodeOutput);

                if (!nodeResult.Success)
                    break;

                // Extract _value from Node
                var valueField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_value");
                if (valueField != null)
                {
                    var resolved = await ResolveElementAsync(
                        manager, valueField.Value, elementType, isInlineable,
                        depth, maxDepth, maxElements, maxStringLength,
                        seenAddresses, rootAddress, result.Elements!.Count, result, cancellationToken);
                    result.Elements.Add(resolved);
                }

                // Move to next node
                var nextField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_next");
                currentNodeAddr = nextField?.Value ?? "";
            }

            result.Count = result.Elements!.Count;
            result.ElementsReturned = result.Elements.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ConcurrentStack extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "ConcurrentStack extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts elements from a ConcurrentBag&lt;T&gt;.
    /// ConcurrentBag uses thread-local lists stored in _locals and a global _workStealingQueues.
    /// </summary>
    public async Task<ExtractionResult> ExtractConcurrentBagElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // ConcurrentBag has _workStealingQueues which is a WorkStealingQueue[]
            var queuesField = fields.FirstOrDefault(f => f.Name == "_workStealingQueues");

            if (queuesField == null || PrimitiveResolver.IsNullAddress(queuesField.Value))
            {
                result.ElementsReturned = 0;
                result.Count = 0;
                return result;
            }

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            // Dump the queues array
            var queuesAddr = PrimitiveResolver.NormalizeAddress(queuesField.Value);
            var queuesOutput = manager.ExecuteCommand($"dumparray {queuesAddr}");
            var queueAddresses = ParseArrayElements(queuesOutput);

            foreach (var queueAddr in queueAddresses)
            {
                if (result.Elements!.Count >= maxElements)
                    break;

                if (PrimitiveResolver.IsNullAddress(queueAddr))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();

                // Each WorkStealingQueue has _array field
                var normalizedQueueAddr = PrimitiveResolver.NormalizeAddress(queueAddr);
                var queueOutput = manager.ExecuteCommand($"dumpobj {normalizedQueueAddr}");
                var queueResult = DumpObjParser.Parse(queueOutput);

                if (!queueResult.Success)
                    continue;

                var arrayField = queueResult.Fields.FirstOrDefault(f => f.Name == "_array");
                if (arrayField == null || PrimitiveResolver.IsNullAddress(arrayField.Value))
                    continue;

                // Get head and tail indices
                var headField = queueResult.Fields.FirstOrDefault(f => f.Name == "_headIndex");
                var tailField = queueResult.Fields.FirstOrDefault(f => f.Name == "_tailIndex");

                if (!int.TryParse(headField?.Value, out var head))
                    head = 0;
                if (!int.TryParse(tailField?.Value, out var tail))
                    tail = 0;

                // Dump the internal array
                var arrayAddr = PrimitiveResolver.NormalizeAddress(arrayField.Value);
                var arrayOutput = manager.ExecuteCommand($"dumparray {arrayAddr}");
                var allElements = ParseArrayElementsByIndex(arrayOutput);

                // Get array length for circular buffer
                var lengthMatch = Regex.Match(arrayOutput, @"Number of elements (\d+)");
                var arrayLength = lengthMatch.Success ? int.Parse(lengthMatch.Groups[1].Value) : allElements.Count;

                // Extract elements from head to tail (circular)
                var count = (tail - head);
                if (count < 0) count += arrayLength;

                for (int i = 0; i < count && result.Elements!.Count < maxElements; i++)
                {
                    var actualIndex = (head + i) % arrayLength;
                    if (allElements.TryGetValue(actualIndex, out var elementValue))
                    {
                        if (!PrimitiveResolver.IsNullAddress(elementValue))
                        {
                            var resolved = await ResolveElementAsync(
                                manager, elementValue, elementType, isInlineable,
                                depth, maxDepth, maxElements, maxStringLength,
                                seenAddresses, rootAddress, result.Elements.Count, result, cancellationToken);
                            result.Elements.Add(resolved);
                        }
                    }
                }
            }

            result.Count = result.Elements!.Count;
            result.ElementsReturned = result.Elements.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ConcurrentBag extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "ConcurrentBag extraction failed");
        }

        return result;
    }



    /// <summary>
    /// Extracts entries from an ImmutableDictionary&lt;K,V&gt;.
    /// ImmutableDictionary uses an AVL tree structure with HashBucket nodes.
    /// </summary>
    public async Task<ExtractionResult> ExtractImmutableDictionaryEntriesAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Entries = new List<CollectionEntry>() };

        try
        {
            // ImmutableDictionary has _root field pointing to the tree root
            var rootField = fields.FirstOrDefault(f => f.Name == "_root");

            if (rootField == null || PrimitiveResolver.IsNullAddress(rootField.Value))
            {
                result.ElementsReturned = 0;
                result.Count = 0;
                return result;
            }

            var kvTypes = CollectionTypeDetector.ExtractKeyValueTypes(collectionTypeName);
            var keyInlineable = kvTypes?.KeyType != null && CollectionTypeDetector.IsInlineableType(kvTypes.Value.KeyType);
            var valueInlineable = kvTypes?.ValueType != null && CollectionTypeDetector.IsInlineableType(kvTypes.Value.ValueType);

            var visited = new HashSet<string>();

            await TraverseImmutableDictionaryAsync(
                manager, rootField.Value, kvTypes, keyInlineable, valueInlineable,
                result.Entries!, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);

            result.Count = result.Entries!.Count;
            result.ElementsReturned = result.Entries.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Entries?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ImmutableDictionary extraction failed: {ex.Message}";
            result.ElementsReturned = result.Entries?.Count ?? 0;
            _logger.LogError(ex, "ImmutableDictionary extraction failed");
        }

        return result;
    }

    private async Task TraverseImmutableDictionaryAsync(
        IDebuggerManager manager,
        string nodeAddr,
        (string KeyType, string ValueType)? kvTypes,
        bool keyInlineable,
        bool valueInlineable,
        List<CollectionEntry> entries,
        int maxElements,
        HashSet<string> visited,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        ExtractionResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(nodeAddr) ||
            PrimitiveResolver.IsNullAddress(nodeAddr) ||
            entries.Count >= maxElements ||
            visited.Contains(nodeAddr))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        visited.Add(nodeAddr);

        var normalizedAddr = PrimitiveResolver.NormalizeAddress(nodeAddr);
        var nodeOutput = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
        var nodeResult = DumpObjParser.Parse(nodeOutput);

        if (!nodeResult.Success)
            return;

        // Traverse left subtree
        var leftField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_left");
        if (leftField != null && !PrimitiveResolver.IsNullAddress(leftField.Value))
        {
            await TraverseImmutableDictionaryAsync(
                manager, leftField.Value, kvTypes, keyInlineable, valueInlineable,
                entries, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);
        }

        // Extract key and value from current node
        if (entries.Count < maxElements)
        {
            var keyField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_key" || f.Name == "Key");
            var valueField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_value" || f.Name == "Value");

            if (keyField != null && valueField != null)
            {
                var resolvedKey = await ResolveElementAsync(
                    manager, keyField.Value, kvTypes?.KeyType, keyInlineable,
                    depth, maxDepth, maxElements, maxStringLength,
                    seenAddresses, rootAddress, entries.Count, result, cancellationToken);

                var resolvedValue = await ResolveElementAsync(
                    manager, valueField.Value, kvTypes?.ValueType, valueInlineable,
                    depth, maxDepth, maxElements, maxStringLength,
                    seenAddresses, rootAddress, entries.Count, result, cancellationToken);

                entries.Add(new CollectionEntry { Key = resolvedKey, Value = resolvedValue });
            }
        }

        // Traverse right subtree
        var rightField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_right");
        if (rightField != null && !PrimitiveResolver.IsNullAddress(rightField.Value))
        {
            await TraverseImmutableDictionaryAsync(
                manager, rightField.Value, kvTypes, keyInlineable, valueInlineable,
                entries, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);
        }
    }



    /// <summary>
    /// Extracts elements from an ImmutableHashSet&lt;T&gt;.
    /// ImmutableHashSet uses an AVL tree structure similar to ImmutableDictionary.
    /// </summary>
    public async Task<ExtractionResult> ExtractImmutableHashSetElementsAsync(
        IDebuggerManager manager,
        string collectionTypeName,
        List<DumpFieldInfo> fields,
        int maxElements,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult { Elements = new List<object?>() };

        try
        {
            // ImmutableHashSet has _root field pointing to the tree root
            var rootField = fields.FirstOrDefault(f => f.Name == "_root");

            if (rootField == null || PrimitiveResolver.IsNullAddress(rootField.Value))
            {
                result.ElementsReturned = 0;
                result.Count = 0;
                return result;
            }

            var elementType = CollectionTypeDetector.ExtractElementType(collectionTypeName);
            var isInlineable = elementType != null && CollectionTypeDetector.IsInlineableType(elementType);

            var visited = new HashSet<string>();

            await TraverseImmutableHashSetAsync(
                manager, rootField.Value, elementType, isInlineable,
                result.Elements!, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);

            result.Count = result.Elements!.Count;
            result.ElementsReturned = result.Elements.Count;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Operation cancelled";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"ImmutableHashSet extraction failed: {ex.Message}";
            result.ElementsReturned = result.Elements?.Count ?? 0;
            _logger.LogError(ex, "ImmutableHashSet extraction failed");
        }

        return result;
    }

    private async Task TraverseImmutableHashSetAsync(
        IDebuggerManager manager,
        string nodeAddr,
        string? elementType,
        bool isInlineable,
        List<object?> elements,
        int maxElements,
        HashSet<string> visited,
        int depth,
        int maxDepth,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        ExtractionResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(nodeAddr) ||
            PrimitiveResolver.IsNullAddress(nodeAddr) ||
            elements.Count >= maxElements ||
            visited.Contains(nodeAddr))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        visited.Add(nodeAddr);

        var normalizedAddr = PrimitiveResolver.NormalizeAddress(nodeAddr);
        var nodeOutput = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
        var nodeResult = DumpObjParser.Parse(nodeOutput);

        if (!nodeResult.Success)
            return;

        // Traverse left subtree
        var leftField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_left");
        if (leftField != null && !PrimitiveResolver.IsNullAddress(leftField.Value))
        {
            await TraverseImmutableHashSetAsync(
                manager, leftField.Value, elementType, isInlineable,
                elements, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);
        }

        // Extract key (the value in HashSet) from current node
        if (elements.Count < maxElements)
        {
            var keyField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_key" || f.Name == "Key");
            if (keyField != null)
            {
                var resolved = await ResolveElementAsync(
                    manager, keyField.Value, elementType, isInlineable,
                    depth, maxDepth, maxElements, maxStringLength,
                    seenAddresses, rootAddress, elements.Count, result, cancellationToken);
                elements.Add(resolved);
            }
        }

        // Traverse right subtree
        var rightField = nodeResult.Fields.FirstOrDefault(f => f.Name == "_right");
        if (rightField != null && !PrimitiveResolver.IsNullAddress(rightField.Value))
        {
            await TraverseImmutableHashSetAsync(
                manager, rightField.Value, elementType, isInlineable,
                elements, maxElements, visited,
                depth, maxDepth, maxStringLength,
                seenAddresses, rootAddress, result, cancellationToken);
        }
    }



    /// <summary>
    /// Parses dumparray output and resolves element values.
    /// </summary>
    private async Task ParseAndResolveArrayElementsAsync(
        IDebuggerManager manager,
        string dumparrayOutput,
        string? elementType,
        bool isInlineable,
        int maxElements,
        int depth,
        int maxDepth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        ExtractionResult result,
        CancellationToken cancellationToken)
    {
        // Pattern for array elements: [index] address (or value for value types)
        var elementPattern = ArrayElementPattern();

        foreach (Match match in elementPattern.Matches(dumparrayOutput))
        {
            if (result.Elements!.Count >= maxElements)
                break;

            cancellationToken.ThrowIfCancellationRequested();

            var index = int.Parse(match.Groups[1].Value);
            var valueStr = match.Groups[2].Value.Trim();

            var resolved = await ResolveElementAsync(
                manager, valueStr, elementType, isInlineable,
                depth, maxDepth, maxArrayElements, maxStringLength,
                seenAddresses, rootAddress, index, result, cancellationToken);

            result.Elements.Add(resolved);
        }
    }

    [GeneratedRegex(@"\[(\d+)\]\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex ArrayElementPattern();

    /// <summary>
    /// Resolves a single element value.
    /// Per the plan: strings and enums should be inlined directly, not wrapped in InspectedObject.
    /// </summary>
    private async Task<object?> ResolveElementAsync(
        IDebuggerManager manager,
        string value,
        string? typeName,
        bool isInlineable,
        int depth,
        int maxDepth,
        int maxArrayElements,
        int maxStringLength,
        HashSet<string> seenAddresses,
        string rootAddress,
        int index,
        ExtractionResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            // Handle null
            if (value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                PrimitiveResolver.IsNullAddress(value))
            {
                return null;
            }

            // For inlineable types, try to resolve directly
            if (isInlineable && typeName != null)
            {
                if (PrimitiveResolver.IsPrimitiveType(typeName))
                {
                    return PrimitiveResolver.ResolvePrimitiveValue(typeName, value);
                }
            }

            // Check if it's an address (reference type) or value (value type)
            if (LooksLikeAddress(value))
            {
                if (depth >= maxDepth)
                {
                    return new Dictionary<string, object> { { "maxDepth", true }, { "address", value } };
                }

                // Check if this is a string - inline it directly per plan
                if (typeName is "System.String" or "String" or "string")
                {
                    return await GetStringValueInlineAsync(manager, value, maxStringLength, cancellationToken);
                }

                // Check if this might be a string by inspecting the object
                // This handles cases where typeName is null or unknown
                var inspectedType = await TryGetObjectTypeAsync(manager, value, cancellationToken);
                if (inspectedType is "System.String")
                {
                    return await GetStringValueInlineAsync(manager, value, maxStringLength, cancellationToken);
                }

                // Check if this is an enum - inline it directly per plan
                if (inspectedType != null && await IsEnumTypeAsync(manager, value, cancellationToken))
                {
                    var enumValue = await GetEnumValueInlineAsync(manager, value, cancellationToken);
                    if (enumValue != null)
                        return enumValue;
                }

                // Reference type - recurse for full InspectedObject
                var element = await _objectInspector.InspectAsync(
                    manager,
                    value,
                    null,
                    depth,
                    maxArrayElements,
                    maxStringLength,
                    cancellationToken);
                return element;
            }

            // Raw value (already resolved)
            return value;
        }
        catch (Exception ex)
        {
            // Record error but continue with other elements
            _logger.LogDebug(ex, "Failed to resolve element at index {Index}", index);

            if (result.Error == null)
            {
                result.Error = $"Failed to inspect element at index {index}: {ex.Message}";
            }

            return new Dictionary<string, object> { { "error", ex.Message }, { "index", index } };
        }
    }

    /// <summary>
    /// Gets a string value directly (inlined) without wrapping in InspectedObject.
    /// </summary>
    private async Task<string?> GetStringValueInlineAsync(
        IDebuggerManager manager,
        string address,
        int maxStringLength,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedAddr = PrimitiveResolver.NormalizeAddress(address);
            var output = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
            var dumpResult = DumpObjParser.Parse(output);

            if (dumpResult.Success && !string.IsNullOrEmpty(dumpResult.StringValue))
            {
                var stringValue = dumpResult.StringValue;
                if (stringValue.Length > maxStringLength)
                {
                    stringValue = $"{stringValue[..maxStringLength]}[truncated: {stringValue.Length} chars]";
                }
                return stringValue;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the type name of an object without full inspection.
    /// </summary>
    private async Task<string?> TryGetObjectTypeAsync(
        IDebuggerManager manager,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedAddr = PrimitiveResolver.NormalizeAddress(address);
            var output = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
            var dumpResult = DumpObjParser.Parse(output);
            return dumpResult.Success ? dumpResult.Name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if an object is an enum type.
    /// </summary>
    private async Task<bool> IsEnumTypeAsync(
        IDebuggerManager manager,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedAddr = PrimitiveResolver.NormalizeAddress(address);
            var output = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
            var dumpResult = DumpObjParser.Parse(output);

            if (!dumpResult.Success || string.IsNullOrEmpty(dumpResult.MethodTable))
                return false;

            // Check if this is an enum by examining the MT
            var mtOutput = manager.ExecuteCommand($"dumpmt {dumpResult.MethodTable}");
            return mtOutput.Contains("System.Enum", StringComparison.OrdinalIgnoreCase) ||
                   mtOutput.Contains("Parent Class") && mtOutput.Contains("Enum");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets an enum value as a string (name or numeric value).
    /// </summary>
    private async Task<object?> GetEnumValueInlineAsync(
        IDebuggerManager manager,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedAddr = PrimitiveResolver.NormalizeAddress(address);
            var output = manager.ExecuteCommand($"dumpobj {normalizedAddr}");
            var dumpResult = DumpObjParser.Parse(output);

            if (!dumpResult.Success)
                return null;

            // Look for value__ field which contains the enum's underlying value
            var valueField = dumpResult.Fields.FirstOrDefault(f => f.Name == "value__");
            if (valueField != null)
            {
                // Try to get the enum name
                if (int.TryParse(valueField.Value, out var intValue))
                {
                    // Return the numeric value if we can't resolve the name
                    // The ObjectInspector's TryGetEnumNameAsync would need to be called here
                    // but for simplicity, we return the value with the type name
                    return intValue;
                }
                return valueField.Value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeAddress(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var clean = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return clean.Length >= 8 && clean.All(c => char.IsAsciiHexDigit(c));
    }

    private Dictionary<int, string> ParseArrayElementsByIndex(string dumparrayOutput)
    {
        var result = new Dictionary<int, string>();
        var pattern = ArrayElementPattern();

        foreach (Match match in pattern.Matches(dumparrayOutput))
        {
            if (int.TryParse(match.Groups[1].Value, out var index))
            {
                result[index] = match.Groups[2].Value.Trim();
            }
        }

        return result;
    }

    private List<string> ParseArrayElements(string dumparrayOutput)
    {
        var result = new List<string>();
        var pattern = ArrayElementPattern();

        foreach (Match match in pattern.Matches(dumparrayOutput))
        {
            result.Add(match.Groups[2].Value.Trim());
        }

        return result;
    }

}
