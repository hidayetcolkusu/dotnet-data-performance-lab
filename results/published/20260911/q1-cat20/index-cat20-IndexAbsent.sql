-- Captured from EF Core, including the parameter types and values it sent.
-- Synthetic lab data only; no connection information is recorded here.
-- parameter @p (Int32, size 0) = 50
-- parameter @categoryId (Int32, size 0) = 20

SELECT TOP(@p) [p].[Id], [p].[Sku], [p].[Name], [p].[CategoryId], [p].[UnitPrice], [p].[CreatedAtUtc]
FROM [Products] AS [p]
WHERE [p].[CategoryId] = @categoryId AND [p].[IsActive] = CAST(1 AS bit)
ORDER BY [p].[CreatedAtUtc], [p].[Id]
