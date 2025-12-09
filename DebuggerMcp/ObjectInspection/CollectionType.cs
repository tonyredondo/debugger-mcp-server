namespace DebuggerMcp.ObjectInspection;

/// <summary>
/// Represents the type of .NET collection for specialized inspection.
/// </summary>
public enum CollectionType
{
    /// <summary>Not a recognized collection type.</summary>
    None = 0,

    // Tier 1: Simple internal array
    /// <summary>T[] - Native array.</summary>
    Array,
    /// <summary>List&lt;T&gt; - Dynamic array.</summary>
    List,
    /// <summary>Stack&lt;T&gt; - LIFO collection.</summary>
    Stack,
    /// <summary>Queue&lt;T&gt; - FIFO collection.</summary>
    Queue,
    /// <summary>HashSet&lt;T&gt; - Unique elements.</summary>
    HashSet,

    // Tier 2: Key-Value collections
    /// <summary>Dictionary&lt;K,V&gt; - Key-value pairs.</summary>
    Dictionary,
    /// <summary>SortedDictionary&lt;K,V&gt; - Sorted key-value pairs.</summary>
    SortedDictionary,
    /// <summary>SortedList&lt;K,V&gt; - Sorted list of key-value pairs.</summary>
    SortedList,

    // Tier 3: Concurrent collections
    /// <summary>ConcurrentDictionary&lt;K,V&gt; - Thread-safe dictionary.</summary>
    ConcurrentDictionary,
    /// <summary>ConcurrentQueue&lt;T&gt; - Thread-safe queue.</summary>
    ConcurrentQueue,
    /// <summary>ConcurrentStack&lt;T&gt; - Thread-safe stack.</summary>
    ConcurrentStack,
    /// <summary>ConcurrentBag&lt;T&gt; - Thread-safe unordered collection.</summary>
    ConcurrentBag,

    // Tier 4: Immutable collections
    /// <summary>ImmutableArray&lt;T&gt; - Immutable array.</summary>
    ImmutableArray,
    /// <summary>ImmutableList&lt;T&gt; - Immutable list.</summary>
    ImmutableList,
    /// <summary>ImmutableDictionary&lt;K,V&gt; - Immutable dictionary.</summary>
    ImmutableDictionary,
    /// <summary>ImmutableHashSet&lt;T&gt; - Immutable hash set.</summary>
    ImmutableHashSet
}

