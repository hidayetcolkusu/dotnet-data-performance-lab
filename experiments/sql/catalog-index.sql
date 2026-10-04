-- Candidate index for the Q1 A/B experiment.
--
-- This is the same index the EF migration AddCatalogIndexAndWiderImportStaging creates in
-- the normal API schema. It is reproduced here so the DDL under test is readable on its own
-- and can be applied by hand against DataPerformanceLab_Experiment.
--
-- The experiment runner (dpl query --experiment index) toggles this index automatically and
-- restores the state it found. Never run this against DataPerformanceLab: the API database
-- keeps the index permanently and receives no experiment DDL.
--
-- The clustered primary key on Products.Id and the unique index on Products.Sku are part of
-- the schema contract and are never dropped by an experiment.

USE DataPerformanceLab_Experiment;
GO

-- Baseline side of Q1: candidate index absent.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.Products')
      AND name = 'IX_Products_Category_Active_Created_Id')
    DROP INDEX IX_Products_Category_Active_Created_Id ON dbo.Products;
GO

-- Candidate side of Q1: covers the filter, the ordering and every list DTO column.
CREATE INDEX IX_Products_Category_Active_Created_Id
ON dbo.Products(CategoryId, IsActive, CreatedAtUtc, Id)
INCLUDE(Sku, Name, UnitPrice);
GO

-- Inspect the current state.
SELECT i.name, i.type_desc, i.is_unique
FROM sys.indexes AS i
WHERE i.object_id = OBJECT_ID('dbo.Products')
ORDER BY i.name;
GO
