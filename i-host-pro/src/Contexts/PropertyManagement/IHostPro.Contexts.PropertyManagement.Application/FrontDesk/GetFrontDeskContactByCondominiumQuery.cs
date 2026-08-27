using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.FrontDesk;

/// <summary>
/// Reads a single Condominium's configured front desk contact (Fase 10,
/// Checkpoint 4). A nonexistent Condominium and a Condominium with no
/// contact configured yet are two DISTINCT failure codes — unlike most
/// "not found" reads in this Bounded Context — since a client configuring
/// this endpoint for the first time needs to tell "condominium id typo"
/// apart from "not configured yet".
/// </summary>
public sealed record GetFrontDeskContactByCondominiumQuery(Guid CondominiumId) : IQuery<FrontDeskContactResult>;
