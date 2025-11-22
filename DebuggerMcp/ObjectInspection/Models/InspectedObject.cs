using System.Text.Json.Serialization;

namespace DebuggerMcp.ObjectInspection.Models;

/// <summary>
/// Represents an inspected .NET object in JSON format.
/// </summary>
public class InspectedObject
{
    /// <summary>
    /// Gets or sets the memory address of the object.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type name of the object.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the method table address.
    /// </summary>
    [JsonPropertyName("mt")]
    public string MethodTable { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is a value type (struct) or reference type (class).
    /// </summary>
    [JsonPropertyName("isValueType")]
    public bool IsValueType { get; set; }

    /// <summary>
    /// Gets or sets the size of the object in bytes.
    /// </summary>
    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Size { get; set; }

    /// <summary>
    /// Gets or sets a human-readable formatted value for types like DateTime, TimeSpan, Guid.
    /// This provides an easily readable representation alongside the raw field data.
    /// </summary>
    [JsonPropertyName("formattedValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormattedValue { get; set; }

    /// <summary>
    /// Gets or sets the fields of the object.
    /// </summary>
    [JsonPropertyName("fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<InspectedField>? Fields { get; set; }

    /// <summary>
    /// Gets or sets the array length (if this is an array).
    /// </summary>
    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Length { get; set; }

    /// <summary>
    /// Gets or sets the array elements (if this is an array or collection).
    /// </summary>
    [JsonPropertyName("elements")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object?>? Elements { get; set; }

    /// <summary>
    /// Gets or sets whether this object is a collection.
    /// </summary>
    [JsonPropertyName("isCollection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCollection { get; set; }

    /// <summary>
    /// Gets or sets the collection type (if IsCollection is true).
    /// </summary>
    [JsonPropertyName("collectionType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollectionKind { get; set; }

    /// <summary>
    /// Gets or sets the number of elements in the collection.
    /// </summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; set; }

    /// <summary>
    /// Gets or sets the capacity of the internal storage (if applicable).
    /// </summary>
    [JsonPropertyName("capacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Capacity { get; set; }

    /// <summary>
    /// Gets or sets the number of elements actually returned in the elements/entries array.
    /// </summary>
    [JsonPropertyName("elementsReturned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ElementsReturned { get; set; }

    /// <summary>
    /// Gets or sets whether the collection was truncated (count > elementsReturned).
    /// </summary>
    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets an extraction error message if extraction failed partially.
    /// </summary>
    [JsonPropertyName("extractionError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtractionError { get; set; }

    /// <summary>
    /// Gets or sets the collection entries (for key-value collections like Dictionary).
    /// </summary>
    [JsonPropertyName("entries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CollectionEntry>? Entries { get; set; }

    /// <summary>
    /// Gets or sets delegate-specific information (if this is a delegate).
    /// </summary>
    [JsonPropertyName("delegate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DelegateInfo? Delegate { get; set; }

    /// <summary>
    /// Gets or sets exception-specific information (if this is an exception).
    /// </summary>
    [JsonPropertyName("exception")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionInfo? Exception { get; set; }

    /// <summary>
    /// Gets or sets task-specific information (if this is a Task).
    /// </summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskInfo? Task { get; set; }

    /// <summary>
    /// Gets or sets type-specific information (if this is a System.Type / RuntimeType).
    /// </summary>
    [JsonPropertyName("typeInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypeReflectionInfo? TypeInfo { get; set; }
}

/// <summary>
/// Represents delegate-specific information.
/// </summary>
public class DelegateInfo
{
    /// <summary>
    /// Gets or sets the target object address.
    /// </summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the method descriptor address.
    /// </summary>
    [JsonPropertyName("methodDesc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodDesc { get; set; }

    /// <summary>
    /// Gets or sets the method name.
    /// </summary>
    [JsonPropertyName("methodName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodName { get; set; }

    /// <summary>
    /// Gets or sets the containing class name.
    /// </summary>
    [JsonPropertyName("className")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassName { get; set; }

    /// <summary>
    /// Gets or sets whether the method is JIT compiled.
    /// </summary>
    [JsonPropertyName("isJitted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsJitted { get; set; }

    /// <summary>
    /// Gets or sets the native code address.
    /// </summary>
    [JsonPropertyName("codeAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CodeAddress { get; set; }
}

/// <summary>
/// Represents exception-specific information.
/// </summary>
public class ExceptionInfo
{
    /// <summary>
    /// Gets or sets the exception message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the HResult.
    /// </summary>
    [JsonPropertyName("hResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HResult { get; set; }

    /// <summary>
    /// Gets or sets the inner exception address.
    /// </summary>
    [JsonPropertyName("innerException")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InnerException { get; set; }

    /// <summary>
    /// Gets or sets the stack trace.
    /// </summary>
    [JsonPropertyName("stackTrace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; set; }
}

/// <summary>
/// Represents task-specific information.
/// </summary>
public class TaskInfo
{
    /// <summary>
    /// Gets or sets the task status (e.g., Running, RanToCompletion, Faulted, Canceled).
    /// </summary>
    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets whether the task is completed.
    /// </summary>
    [JsonPropertyName("isCompleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsCompleted { get; set; }

    /// <summary>
    /// Gets or sets whether the task completed successfully.
    /// </summary>
    [JsonPropertyName("isCompletedSuccessfully")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsCompletedSuccessfully { get; set; }

    /// <summary>
    /// Gets or sets whether the task was canceled.
    /// </summary>
    [JsonPropertyName("isCanceled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsCanceled { get; set; }

    /// <summary>
    /// Gets or sets whether the task is faulted.
    /// </summary>
    [JsonPropertyName("isFaulted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFaulted { get; set; }

    /// <summary>
    /// Gets or sets the task ID.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; set; }

    /// <summary>
    /// Gets or sets the result address (for Task&lt;T&gt;).
    /// </summary>
    [JsonPropertyName("resultAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultAddress { get; set; }

    /// <summary>
    /// Gets or sets the exception address (if faulted).
    /// </summary>
    [JsonPropertyName("exceptionAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionAddress { get; set; }

    /// <summary>
    /// Gets or sets the async state machine type (if available).
    /// </summary>
    [JsonPropertyName("stateMachineType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateMachineType { get; set; }

    /// <summary>
    /// Gets or sets the current state of the async state machine.
    /// </summary>
    [JsonPropertyName("stateMachineState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StateMachineState { get; set; }
}

/// <summary>
/// Represents type reflection information (for System.Type objects).
/// </summary>
public class TypeReflectionInfo
{
    /// <summary>
    /// Gets or sets the full name of the type (e.g., "System.Collections.Generic.List`1[[System.String]]").
    /// </summary>
    [JsonPropertyName("fullName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullName { get; set; }

    /// <summary>
    /// Gets or sets the namespace of the type.
    /// </summary>
    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets the assembly name.
    /// </summary>
    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Assembly { get; set; }

    /// <summary>
    /// Gets or sets the method table address for the type.
    /// </summary>
    [JsonPropertyName("methodTable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodTable { get; set; }

    /// <summary>
    /// Gets or sets the base type name.
    /// </summary>
    [JsonPropertyName("baseType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseType { get; set; }

    /// <summary>
    /// Gets or sets whether this is a value type.
    /// </summary>
    [JsonPropertyName("isValueType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsValueType { get; set; }

    /// <summary>
    /// Gets or sets whether this is an interface.
    /// </summary>
    [JsonPropertyName("isInterface")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsInterface { get; set; }

    /// <summary>
    /// Gets or sets whether this is an enum.
    /// </summary>
    [JsonPropertyName("isEnum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsEnum { get; set; }

    /// <summary>
    /// Gets or sets whether this is an array type.
    /// </summary>
    [JsonPropertyName("isArray")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsArray { get; set; }

    /// <summary>
    /// Gets or sets whether this is a generic type.
    /// </summary>
    [JsonPropertyName("isGeneric")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsGeneric { get; set; }

    /// <summary>
    /// Gets or sets the number of methods in the type.
    /// </summary>
    [JsonPropertyName("methodCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MethodCount { get; set; }

    /// <summary>
    /// Gets or sets the number of interfaces implemented.
    /// </summary>
    [JsonPropertyName("interfaceCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InterfaceCount { get; set; }

    /// <summary>
    /// Gets or sets the base size of instances.
    /// </summary>
    [JsonPropertyName("baseSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BaseSize { get; set; }

    /// <summary>
    /// Gets or sets the file path where the type is defined.
    /// </summary>
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }
}

/// <summary>
/// Represents an inspected field in JSON format.
/// </summary>
public class InspectedField
{
    /// <summary>
    /// Gets or sets the field name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type name of the field.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is a static field.
    /// </summary>
    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }

    /// <summary>
    /// Gets or sets the value of the field.
    /// Can be: primitive value, string, InspectedObject, "[this]", "[seen]", "[max depth]", "[error: message]", or null.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

/// <summary>
/// Represents a key-value entry in a dictionary-like collection.
/// </summary>
public class CollectionEntry
{
    /// <summary>
    /// Gets or sets the key.
    /// Can be: primitive value, string, InspectedObject, or null.
    /// </summary>
    [JsonPropertyName("key")]
    public object? Key { get; set; }
    
    /// <summary>
    /// Gets or sets the value.
    /// Can be: primitive value, string, InspectedObject, or null.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

