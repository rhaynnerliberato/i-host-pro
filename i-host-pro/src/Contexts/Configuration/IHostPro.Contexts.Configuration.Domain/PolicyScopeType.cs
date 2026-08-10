namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// The levels at which a policy value can be defined, in resolution
/// precedence order (<see cref="Property"/> wins over <see cref="Tenant"/>,
/// which wins over <see cref="Global"/>) — Fase 5, Incremento 1 official
/// decision 1: hierarquia inicial de 3 níveis (Grupo de Imóveis e Condomínio
/// ficam de fora do MVP). Persisted via <c>HasConversion&lt;string&gt;()</c>
/// (a plain <c>varchar</c> column, never a native PostgreSQL <c>ENUM</c>
/// type) specifically so a future level can be added without a destructive
/// migration, per decision 1: "Não usar PostgreSQL enum rígido... Persistir
/// o código do escopo de forma validada e extensível."
/// </summary>
public enum PolicyScopeType
{
    Global,
    Tenant,
    Property,
}
