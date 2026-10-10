-- A SQL Server database exercising every type, default form, key and index shape the
-- SQL Server → PostgreSQL path handles, plus the cases it must report rather than migrate.
-- Batches are separated by GO lines.

CREATE SCHEMA sales;
GO
CREATE TYPE dbo.PhoneNumber FROM varchar(20) NULL;
GO

CREATE TABLE dbo.Customers (
    CustomerID int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Email varchar(255) NULL CONSTRAINT UQ_Customers_Email UNIQUE,
    Phone dbo.PhoneNumber NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Customers_IsActive DEFAULT ((1)),
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (sysutcdatetime()),
    RowGuid uniqueidentifier NOT NULL CONSTRAINT DF_Customers_RowGuid DEFAULT (newid()),
    CreditLimit money NULL CONSTRAINT DF_Customers_CreditLimit DEFAULT ((0)),
    Notes nvarchar(max) NULL,
    ReferredByID int NULL CONSTRAINT FK_Customers_ReferredBy REFERENCES dbo.Customers (CustomerID),
    Version rowversion NOT NULL
);
CREATE INDEX IX_Customers_Name ON dbo.Customers (Name);

CREATE TABLE sales.[Order Header] (
    OrderID bigint IDENTITY(1000, 5) NOT NULL CONSTRAINT PK_OrderHeader PRIMARY KEY,
    CustomerID int NOT NULL CONSTRAINT FK_OrderHeader_Customers REFERENCES dbo.Customers (CustomerID) ON DELETE CASCADE,
    OrderDate date NOT NULL CONSTRAINT DF_OrderHeader_OrderDate DEFAULT (CONVERT(date, getdate())),
    Status char(1) NOT NULL CONSTRAINT DF_OrderHeader_Status DEFAULT ('N'),
    Total decimal(18, 2) NOT NULL CONSTRAINT DF_OrderHeader_Total DEFAULT ((0)),
    Discount AS (Total * 0.1),
    ShippedAt datetimeoffset(3) NULL,
    CONSTRAINT CK_OrderHeader_Total CHECK (Total >= 0)
);
CREATE INDEX IX_OrderHeader_Customer ON sales.[Order Header] (CustomerID, OrderDate DESC) INCLUDE (Total);
CREATE INDEX IX_OrderHeader_Open ON sales.[Order Header] (Status) WHERE Status = 'N';

CREATE TABLE sales.OrderLine (
    OrderID bigint NOT NULL CONSTRAINT FK_OrderLine_Order REFERENCES sales.[Order Header] (OrderID) ON DELETE CASCADE,
    LineNumber smallint NOT NULL,
    ProductCode nvarchar(20) NOT NULL,
    Quantity int NOT NULL CONSTRAINT DF_OrderLine_Quantity DEFAULT 1,
    CONSTRAINT PK_OrderLine PRIMARY KEY (OrderID, LineNumber)
);

CREATE TABLE dbo.AllTypes (
    Id int NOT NULL CONSTRAINT PK_AllTypes PRIMARY KEY,
    c_bit bit NULL,
    c_tinyint tinyint NULL,
    c_smallint smallint NULL,
    c_int int NULL,
    c_bigint bigint NULL,
    c_decimal decimal(28, 10) NULL,
    c_numeric numeric(5, 2) NULL,
    c_money money NULL,
    c_smallmoney smallmoney NULL,
    c_real real NULL,
    c_float float NULL,
    c_char char(10) NULL,
    c_varchar varchar(50) NULL,
    c_varcharmax varchar(max) NULL,
    c_nchar nchar(5) NULL,
    c_nvarchar nvarchar(50) NULL,
    c_nvarcharmax nvarchar(max) NULL,
    c_text text NULL,
    c_ntext ntext NULL,
    c_binary binary(4) NULL,
    c_varbinary varbinary(16) NULL,
    c_varbinarymax varbinary(max) NULL,
    c_image image NULL,
    c_date date NULL,
    c_time time(7) NULL,
    c_datetime datetime NULL,
    c_datetime2 datetime2(7) NULL,
    c_smalldatetime smalldatetime NULL,
    c_datetimeoffset datetimeoffset(7) NULL,
    c_uniqueidentifier uniqueidentifier NULL,
    c_xml xml NULL,
    c_sql_variant sql_variant NULL,
    c_hierarchyid hierarchyid NULL,
    c_geography geography NULL,
    c_sysname sysname NULL
);

-- Index names repeat across tables; PostgreSQL needs them unique per schema.
CREATE TABLE dbo.Warehouses (WarehouseID int NOT NULL PRIMARY KEY, Code nvarchar(10) NOT NULL);
CREATE INDEX IX_Code ON dbo.Warehouses (Code);
CREATE TABLE dbo.Suppliers (SupplierID int NOT NULL PRIMARY KEY, Code nvarchar(10) NULL);
CREATE UNIQUE INDEX IX_Code ON dbo.Suppliers (Code);

CREATE TABLE dbo.ThisTableNameIsDeliberatelyLongerThanSixtyThreeBytesToTestShortening (Id int NOT NULL PRIMARY KEY);

CREATE TABLE dbo.Audit (AuditID int NOT NULL PRIMARY KEY, CustomerID int NULL);

-- SSMS creates this for database diagrams; it is never migrated, and neither are hypothetical
-- indexes the tuning advisor leaves behind.
CREATE TABLE dbo.sysdiagrams (diagram_id int IDENTITY PRIMARY KEY, name sysname NOT NULL, definition varbinary(max) NULL);
CREATE INDEX IX_Audit_Hypothetical ON dbo.Audit (CustomerID) WITH STATISTICS_ONLY = -1;
GO

INSERT INTO dbo.Customers (Name, Email, Phone, Notes)
VALUES (N'Ada', 'ada@example.com', '555-0100', NULL),
       (N'Grace', NULL, NULL, N'notes'),
       (N'Linus', 'linus@example.com', NULL, NULL);
UPDATE dbo.Customers SET ReferredByID = 1 WHERE CustomerID = 2;

INSERT INTO sales.[Order Header] (CustomerID, OrderDate, Status, Total, ShippedAt)
VALUES (1, '2024-01-15', 'S', 100.50, '2024-01-16 10:00:00 +02:00'),
       (1, '2024-02-01', 'N', 20.00, NULL),
       (2, '2024-03-01', 'N', 0, NULL);

INSERT INTO sales.OrderLine (OrderID, LineNumber, ProductCode, Quantity)
VALUES (1000, 1, N'P-1', 2), (1000, 2, N'P-2', 1), (1005, 1, N'P-1', 5);

INSERT INTO dbo.Warehouses VALUES (1, N'A'), (2, N'B');
INSERT INTO dbo.Suppliers VALUES (1, N'S1'), (2, NULL);

-- A row the foreign key below never checked.
INSERT INTO dbo.Audit VALUES (1, 999), (2, 1);
ALTER TABLE dbo.Audit WITH NOCHECK ADD CONSTRAINT FK_Audit_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID);

INSERT INTO dbo.AllTypes (Id) VALUES (1);
INSERT INTO dbo.AllTypes VALUES (
    2, 1, 255, -32768, 2147483647, -9223372036854775808,
    123456789012345678.1234567890, 999.99, 922337203685477.5807, -214748.3648,
    3.25, 2.718281828459045,
    'abc', 'hello', REPLICATE(CONVERT(varchar(max), 'x'), 10000),
    N'ñ', N'Grüße 🎉', N'line1' + NCHAR(0) + N'line2',
    'legacy text', N'legacy ntext ✓',
    0x01020304, 0xDEADBEEF, CONVERT(varbinary(max), REPLICATE(CONVERT(varchar(max), 'ab'), 5000)), 0xCAFE,
    '0001-01-01', '23:59:59.1234567', '1753-01-01 00:00:00.003', '9999-12-31 23:59:59.9999999',
    '2079-06-06 23:59', '2024-02-29 12:34:56.1234567 +05:30',
    '6F9619FF-8B86-D011-B42D-00C04FC964FF', N'<root><item id="1">x</item></root>', CAST(42 AS sql_variant),
    hierarchyid::Parse('/1/3/'), geography::Point(47.651, -122.349, 4326), N'my_sysname');
GO
