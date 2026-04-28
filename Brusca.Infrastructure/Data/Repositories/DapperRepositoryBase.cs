using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Brusca.Core.Models;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Base class for all Dapper repositories.
/// All queries MUST call stored procedures — no inline SQL is permitted.
/// Convention: schema.usp_EntityAction  e.g. cleaning.usp_Cleaning_Create
/// </summary>
public abstract class DapperRepositoryBase
{
    private readonly string _connectionString;

    protected DapperRepositoryBase(IOptions<BruscaOptions> options)
    {
        _connectionString = options.Value.DatabaseConnectionString
            ?? throw new InvalidOperationException("DatabaseConnectionString is not configured.");
    }

    protected async Task<T?> QuerySingleOrDefaultAsync<T>(
        string storedProcedure,
        object? parameters = null,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<T>(
            storedProcedure,
            parameters,
            commandType: System.Data.CommandType.StoredProcedure);
    }

    protected async Task<IEnumerable<T>> QueryAsync<T>(
        string storedProcedure,
        object? parameters = null,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QueryAsync<T>(
            storedProcedure,
            parameters,
            commandType: System.Data.CommandType.StoredProcedure);
    }

    protected async Task ExecuteAsync(
        string storedProcedure,
        object? parameters = null,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            storedProcedure,
            parameters,
            commandType: System.Data.CommandType.StoredProcedure);
    }

    protected async Task<T?> ExecuteScalarAsync<T>(
        string storedProcedure,
        object? parameters = null,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<T>(
            storedProcedure,
            parameters,
            commandType: System.Data.CommandType.StoredProcedure);
    }

    /// <summary>
    /// Opens a SqlConnection bound to the configured connection string.
    /// Caller is responsible for disposing it. Used by repositories that need
    /// multi-result-set reads via <see cref="SqlMapper.QueryMultipleAsync"/>.
    /// </summary>
    protected SqlConnection OpenConnection() => new(_connectionString);
}
