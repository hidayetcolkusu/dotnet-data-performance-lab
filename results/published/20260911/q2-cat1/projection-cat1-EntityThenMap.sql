-- Captured from EF Core, including the parameter types and values it sent.
-- Synthetic lab data only; no connection information is recorded here.
-- parameter @p (Int32, size 0) = 50
-- parameter @categoryId (Int32, size 0) = 1

SELECT TOP(@p) [p].[Id], [p].[CategoryId], [p].[CategoryId1], [p].[CreatedAtUtc], [p].[Description], [p].[IsActive], [p].[Name], [p].[Sku], [p].[UnitPrice], [p].[Version]
FROM [Products] AS [p]
WHERE [p].[CategoryId] = @categoryId AND [p].[IsActive] = CAST(1 AS bit)
ORDER BY [p].[CreatedAtUtc], [p].[Id]
