using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSharp2.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase4FulfilmentAndPhase5Finance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements");

            migrationBuilder.AddColumn<Guid>(
                name: "ShipmentItemId",
                table: "StockMovements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockReservationId",
                table: "StockMovements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GoodsReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.CheckConstraint("CK_Invoices_Source", "([Type] = 'Receivable' AND [SalesOrderId] IS NOT NULL AND [GoodsReceiptId] IS NULL) OR ([Type] = 'Payable' AND [SalesOrderId] IS NULL AND [GoodsReceiptId] IS NOT NULL)");
                    table.CheckConstraint("CK_Invoices_Status", "([Status] = 'Draft' AND [IssuedAtUtc] IS NULL) OR ([Status] IN ('Issued','Paid') AND [IssuedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_Invoices_Total", "[Total] >= 0");
                    table.ForeignKey(
                        name: "FK_Invoices_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_GoodsReceipts_GoodsReceiptId",
                        column: x => x.GoodsReceiptId,
                        principalTable: "GoodsReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TrackingNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DeliveryAddress = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.CheckConstraint("CK_Shipments_State", "([Status] IN ('Prepared','Cancelled') AND [DispatchedAtUtc] IS NULL AND [DeliveredAtUtc] IS NULL) OR ([Status] = 'Dispatched' AND [DispatchedAtUtc] IS NOT NULL AND [DeliveredAtUtc] IS NULL) OR ([Status] = 'Delivered' AND [DispatchedAtUtc] IS NOT NULL AND [DeliveredAtUtc] IS NOT NULL AND [DeliveredAtUtc] >= [DispatchedAtUtc])");
                    table.ForeignKey(
                        name: "FK_Shipments_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesOrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservations", x => x.Id);
                    table.CheckConstraint("CK_StockReservations_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_StockReservations_Status", "[Status] IN ('Active','Released','Consumed')");
                    table.ForeignKey(
                        name: "FK_StockReservations_SalesOrderItems_SalesOrderItemId",
                        column: x => x.SalesOrderItemId,
                        principalTable: "SalesOrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockReservations_StockLots_StockLotId",
                        column: x => x.StockLotId,
                        principalTable: "StockLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesOrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GoodsReceiptItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceItems", x => x.Id);
                    table.CheckConstraint("CK_InvoiceItems_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [LineTotal] >= 0 AND [LineTotal] = ROUND([Quantity] * [UnitPrice], 2)");
                    table.CheckConstraint("CK_InvoiceItems_Source", "([SalesOrderItemId] IS NOT NULL AND [GoodsReceiptItemId] IS NULL) OR ([SalesOrderItemId] IS NULL AND [GoodsReceiptItemId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_InvoiceItems_GoodsReceiptItems_GoodsReceiptItemId",
                        column: x => x.GoodsReceiptItemId,
                        principalTable: "GoodsReceiptItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceItems_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceItems_SalesOrderItems_SalesOrderItemId",
                        column: x => x.SalesOrderItemId,
                        principalTable: "SalesOrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payments_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_Payments_AppUsers_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentItems_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentItems_StockReservations_StockReservationId",
                        column: x => x.StockReservationId,
                        principalTable: "StockReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ShipmentItemId_Type",
                table: "StockMovements",
                columns: new[] { "ShipmentItemId", "Type" },
                unique: true,
                filter: "[ShipmentItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_StockReservationId_Type",
                table: "StockMovements",
                columns: new[] { "StockReservationId", "Type" },
                unique: true,
                filter: "[StockReservationId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements",
                sql: "([ReservedDelta] = 0 AND [StockReservationId] IS NULL AND [ShipmentItemId] IS NULL AND\n  (([Type] = 'Receipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NOT NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NULL)\n  OR ([Type] = 'OutputReceipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NOT NULL AND [MaterialIssueItemId] IS NULL)\n  OR ([Type] = 'MaterialIssue' AND [OnHandDelta] < 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NOT NULL)))\nOR ([GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NULL AND\n  (([Type] = 'Reserve' AND [OnHandDelta] = 0 AND [ReservedDelta] > 0 AND [StockReservationId] IS NOT NULL AND [ShipmentItemId] IS NULL)\n  OR ([Type] = 'Release' AND [OnHandDelta] = 0 AND [ReservedDelta] < 0 AND [StockReservationId] IS NOT NULL AND [ShipmentItemId] IS NULL)\n  OR ([Type] = 'Dispatch' AND [OnHandDelta] < 0 AND [ReservedDelta] = [OnHandDelta] AND [StockReservationId] IS NULL AND [ShipmentItemId] IS NOT NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItems_GoodsReceiptItemId",
                table: "InvoiceItems",
                column: "GoodsReceiptItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItems_InvoiceId_GoodsReceiptItemId",
                table: "InvoiceItems",
                columns: new[] { "InvoiceId", "GoodsReceiptItemId" },
                unique: true,
                filter: "[GoodsReceiptItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItems_InvoiceId_SalesOrderItemId",
                table: "InvoiceItems",
                columns: new[] { "InvoiceId", "SalesOrderItemId" },
                unique: true,
                filter: "[SalesOrderItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItems_SalesOrderItemId",
                table: "InvoiceItems",
                column: "SalesOrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_DepartmentId_Status_CreatedAtUtc_Id",
                table: "Invoices",
                columns: new[] { "DepartmentId", "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_GoodsReceiptId",
                table: "Invoices",
                column: "GoodsReceiptId",
                unique: true,
                filter: "[GoodsReceiptId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number",
                table: "Invoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_SalesOrderId",
                table: "Invoices",
                column: "SalesOrderId",
                unique: true,
                filter: "[SalesOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_InvoiceId_RecordedAtUtc_Id",
                table: "Payments",
                columns: new[] { "InvoiceId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RecordedByUserId",
                table: "Payments",
                column: "RecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Reference",
                table: "Payments",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentItems_ShipmentId",
                table: "ShipmentItems",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentItems_StockReservationId",
                table: "ShipmentItems",
                column: "StockReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_DepartmentId_Status_Id",
                table: "Shipments",
                columns: new[] { "DepartmentId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_Number",
                table: "Shipments",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_SalesOrderId",
                table: "Shipments",
                column: "SalesOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_SalesOrderItemId_StockLotId",
                table: "StockReservations",
                columns: new[] { "SalesOrderItemId", "StockLotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_StockLotId_Status",
                table: "StockReservations",
                columns: new[] { "StockLotId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_ShipmentItems_ShipmentItemId",
                table: "StockMovements",
                column: "ShipmentItemId",
                principalTable: "ShipmentItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_StockReservations_StockReservationId",
                table: "StockMovements",
                column: "StockReservationId",
                principalTable: "StockReservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [Invoices]) OR EXISTS (SELECT 1 FROM [Payments])
                    OR EXISTS (SELECT 1 FROM [Shipments]) OR EXISTS (SELECT 1 FROM [StockReservations])
                    THROW 51003, 'Phases 4 and 5 cannot be downgraded while fulfilment or finance documents exist. Restore an appropriate backup instead of removing operational history.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_ShipmentItems_ShipmentItemId",
                table: "StockMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_StockReservations_StockReservationId",
                table: "StockMovements");

            migrationBuilder.DropTable(
                name: "InvoiceItems");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "ShipmentItems");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "Shipments");

            migrationBuilder.DropTable(
                name: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_ShipmentItemId_Type",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_StockReservationId_Type",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "ShipmentItemId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "StockReservationId",
                table: "StockMovements");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements",
                sql: "[ReservedDelta] = 0 AND (([Type] = 'Receipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NOT NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NULL) OR ([Type] = 'OutputReceipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NOT NULL AND [MaterialIssueItemId] IS NULL) OR ([Type] = 'MaterialIssue' AND [OnHandDelta] < 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NOT NULL))");
        }
    }
}
