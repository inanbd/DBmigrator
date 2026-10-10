-- A small SQL Server database for re-run, CLI and conflict tests.

CREATE TABLE dbo.Customers (
    CustomerID int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name nvarchar(100) NOT NULL
);

CREATE TABLE dbo.Orders (
    OrderID int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerID int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (CustomerID),
    Amount decimal(10, 2) NOT NULL
);

-- No primary key, so appending its rows a second time is allowed.
CREATE TABLE dbo.EventLog (Message nvarchar(200) NOT NULL);
GO

INSERT INTO dbo.Customers (Name) VALUES (N'Ada'), (N'Grace');
INSERT INTO dbo.Orders (CustomerID, Amount) VALUES (1, 10.00), (1, 20.00), (2, 5.50);
INSERT INTO dbo.EventLog VALUES (N'started'), (N'stopped');
GO
