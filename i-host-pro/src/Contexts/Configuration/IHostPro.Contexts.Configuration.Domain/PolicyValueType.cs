namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// The shape of a <see cref="PolicyDefinition"/>'s value. Both policies in
/// the Fase 5, Incremento 1 catalog (<c>EARLY_CHECKIN</c>, <c>LATE_CHECKOUT</c>)
/// are declared as <c>type: object</c> — only <see cref="Object"/> exists
/// today. Persisted via <c>HasConversion&lt;string&gt;()</c> (a plain
/// <c>varchar</c> column, never a native PostgreSQL <c>ENUM</c> type), same
/// extensibility convention as <see cref="PolicyScopeType"/>, so a future
/// value type can be added without a destructive migration.
/// </summary>
public enum PolicyValueType
{
    Object,
}
