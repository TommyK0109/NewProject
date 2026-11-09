SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @now datetime2 = SYSUTCDATETIME();
SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n
INTO #n FROM sys.all_objects a CROSS JOIN sys.all_objects b;
SELECT n, NEWID() Id INTO #p FROM #n WHERE n <= 10000;
CREATE UNIQUE CLUSTERED INDEX IX_p ON #p(n);
INSERT Products (Id,Sku,Name,ItemType,BaseUnit,SellingPrice,ReorderLevel,IsActive)
SELECT Id, CONCAT('LOAD-',n), CONCAT('Load product ',n), IIF(n<=5000,'RawMaterial','FinishedGood'), 'piece',100,0,1 FROM #p;
DECLARE @customer uniqueidentifier=NEWID(), @supplier uniqueidentifier=NEWID();
INSERT Customers (Id,Code,Name,DeliveryAddress,IsActive) VALUES(@customer,'LOAD-CUSTOMER','Synthetic load customer','Load test address',1);
INSERT Suppliers (Id,Code,Name,IsActive) VALUES(@supplier,'LOAD-SUPPLIER','Synthetic load supplier',1);
SELECT n,NEWID() Id,NEWID() LineId INTO #s FROM #n;
CREATE UNIQUE CLUSTERED INDEX IX_s ON #s(n);
INSERT SalesOrders(Id,Number,DepartmentId,CustomerId,Status,RequiredByUtc,CreatedAtUtc)
SELECT Id,CONCAT('LOAD-SO-',n),@sales,@customer,IIF(n<=25000,'Confirmed','Cancelled'),DATEADD(day,30,@now),@now FROM #s;
INSERT SalesOrderItems(Id,SalesOrderId,ProductId,Quantity,UnitPrice)
SELECT s.LineId,s.Id,p.Id,1,100 FROM #s s JOIN #p p ON p.n=5001+(s.n-1)%5000;
SELECT n,NEWID() Id INTO #b FROM #n WHERE n<=5000;
CREATE UNIQUE CLUSTERED INDEX IX_b ON #b(n);
INSERT BillOfMaterials(Id,FinishedProductId,Revision,Status,ApprovedByUserId,ApprovedAtUtc)
SELECT b.Id,p.Id,1,'Approved',@approver,@now FROM #b b JOIN #p p ON p.n=b.n+5000;
INSERT BomItems(Id,BillOfMaterialId,MaterialProductId,QuantityPerUnit)
SELECT NEWID(),b.Id,p.Id,1 FROM #b b JOIN #p p ON p.n=b.n;
SELECT n,NEWID() PO,NEWID() POLine,NEWID() GR,NEWID() GRLine,NEWID() RawLot,
 NEWID() Work,NEWID() Requirement,NEWID() Issue,NEWID() IssueItem,NEWID() Output,NEWID() FinishedLot
INTO #r FROM #n WHERE n<=25000;
CREATE UNIQUE CLUSTERED INDEX IX_r ON #r(n);
INSERT PurchaseOrders(Id,Number,DepartmentId,SupplierId,Status,CreatedByUserId,ApprovedByUserId,CreatedAtUtc,ApprovedAtUtc)
SELECT PO,CONCAT('LOAD-PO-',n),@purchasing,@supplier,'Received',@actor,@approver,@now,@now FROM #r;
INSERT PurchaseOrderItems(Id,PurchaseOrderId,ProductId,Quantity,CancelledQuantity,UnitCost)
SELECT r.POLine,r.PO,p.Id,1000,0,10 FROM #r r JOIN #p p ON p.n=1+(r.n-1)%5000;
INSERT GoodsReceipts(Id,Number,DepartmentId,PurchaseOrderId,PostedByUserId,PostedAtUtc)
SELECT GR,CONCAT('LOAD-GR-',n),@warehouse,PO,@actor,@now FROM #r;
INSERT GoodsReceiptItems(Id,GoodsReceiptId,PurchaseOrderItemId,ReceivedQuantity)
SELECT GRLine,GR,POLine,1000 FROM #r;
INSERT StockLots(Id,LotNumber,ProductId,GoodsReceiptItemId,QualityStatus,ReceivedAtUtc)
SELECT r.RawLot,CONCAT('LOAD-RAW-',r.n),p.Id,r.GRLine,'Released',@now FROM #r r JOIN #p p ON p.n=1+(r.n-1)%5000;
INSERT InventoryBalances(StockLotId,OnHand,Reserved) SELECT RawLot,999,0 FROM #r;
INSERT QualityInspections(Id,StockLotId,DepartmentId,Status,InspectedByUserId,InspectedAtUtc,Notes)
SELECT NEWID(),RawLot,@quality,'Pass',@inspector,@now,'Synthetic load fixture' FROM #r;
INSERT ProductionOrders(Id,Number,DepartmentId,SalesOrderItemId,BillOfMaterialId,PlannedQuantity,ScheduledStartUtc,ScheduledEndUtc,CreatedAtUtc,Status)
SELECT r.Work,CONCAT('LOAD-WO-',r.n),@production,s.LineId,b.Id,1,@now,@now,@now,'Completed'
FROM #r r JOIN #s s ON s.n=r.n JOIN #b b ON b.n=1+(r.n-1)%5000;
INSERT ProductionRequirements(Id,ProductionOrderId,MaterialProductId,RequiredQuantity)
SELECT r.Requirement,r.Work,p.Id,1 FROM #r r JOIN #p p ON p.n=1+(r.n-1)%5000;
INSERT MaterialIssues(Id,ProductionOrderId,PostedByUserId,PostedAtUtc) SELECT Issue,Work,@actor,@now FROM #r;
INSERT MaterialIssueItems(Id,MaterialIssueId,ProductionRequirementId,StockLotId,Quantity)
SELECT IssueItem,Issue,Requirement,RawLot,1 FROM #r;
INSERT ProductionOutputs(Id,ProductionOrderId,ProducedQuantity,ScrapQuantity,PostedByUserId,PostedAtUtc)
SELECT Output,Work,1,0,@actor,@now FROM #r;
INSERT StockLots(Id,LotNumber,ProductId,ProductionOutputId,QualityStatus,ReceivedAtUtc)
SELECT r.FinishedLot,CONCAT('LOAD-FG-',r.n),p.Id,r.Output,'Released',@now FROM #r r JOIN #p p ON p.n=5001+(r.n-1)%5000;
INSERT InventoryBalances(StockLotId,OnHand,Reserved) SELECT FinishedLot,1,0 FROM #r;
INSERT QualityInspections(Id,StockLotId,DepartmentId,Status,InspectedByUserId,InspectedAtUtc,Notes)
SELECT NEWID(),FinishedLot,@quality,'Pass',@inspector,@now,'Synthetic load fixture' FROM #r;
SELECT n,NEWID() Id,25001+(n-1)%75000 SalesN,
 IIF(n<=75000,1+(n-1)%25000,1+(n-1+5000)%25000) LotN,
 CAST(IIF(n<=12500 OR n>75000,0.5,1) AS decimal(18,3)) Quantity
INTO #v FROM #n WHERE n<=87500;
INSERT StockReservations(Id,SalesOrderItemId,StockLotId,Quantity,Status)
SELECT v.Id,s.LineId,r.FinishedLot,v.Quantity,'Released' FROM #v v JOIN #s s ON s.n=v.SalesN JOIN #r r ON r.n=v.LotN;
INSERT StockMovements(Id,StockLotId,Type,OnHandDelta,ReservedDelta,GoodsReceiptItemId,PostedAtUtc)
SELECT NEWID(),RawLot,'Receipt',1000,0,GRLine,@now FROM #r;
INSERT StockMovements(Id,StockLotId,Type,OnHandDelta,ReservedDelta,MaterialIssueItemId,PostedAtUtc)
SELECT NEWID(),RawLot,'MaterialIssue',-1,0,IssueItem,@now FROM #r;
INSERT StockMovements(Id,StockLotId,Type,OnHandDelta,ReservedDelta,ProductionOutputId,PostedAtUtc)
SELECT NEWID(),FinishedLot,'OutputReceipt',1,0,Output,@now FROM #r;
INSERT StockMovements(Id,StockLotId,Type,OnHandDelta,ReservedDelta,StockReservationId,PostedAtUtc)
SELECT NEWID(),r.FinishedLot,'Reserve',0,v.Quantity,v.Id,@now FROM #v v JOIN #r r ON r.n=v.LotN;
INSERT StockMovements(Id,StockLotId,Type,OnHandDelta,ReservedDelta,StockReservationId,PostedAtUtc)
SELECT NEWID(),r.FinishedLot,'Release',0,-v.Quantity,v.Id,@now FROM #v v JOIN #r r ON r.n=v.LotN;
COMMIT;
