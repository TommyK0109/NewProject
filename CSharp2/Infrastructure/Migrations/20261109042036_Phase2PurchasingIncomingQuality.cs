using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSharp2.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase2PurchasingIncomingQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 1 exposed no source-posting API. Do not invent purchasing history for placeholder rows.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [GoodsReceiptItems])
                    THROW 51002, 'Phase 2 requires an empty Phase 1 GoodsReceiptItems table. Existing placeholder sources need an explicit data migration before upgrading.', 1;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "GoodsReceiptId",
                table: "GoodsReceiptItems",
                type: "uniqueidentifier",
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseOrderItemId",
                table: "GoodsReceiptItems",
                type: "uniqueidentifier",
                nullable: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceivedQuantity",
                table: "GoodsReceiptItems",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false);

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrders_Approver", "([ApprovedByUserId] IS NULL AND [ApprovedAtUtc] IS NULL AND [Status] IN ('Draft','Cancelled')) OR ([ApprovedByUserId] IS NOT NULL AND [ApprovedAtUtc] IS NOT NULL AND [ApprovedByUserId] <> [CreatedByUserId] AND [Status] IN ('Approved','PartiallyReceived','Received','Cancelled','Closed'))");
                    table.CheckConstraint("CK_PurchaseOrders_Status", "[Status] IN ('Draft','Approved','PartiallyReceived','Received','Cancelled','Closed')");
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_AppUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_AppUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InspectedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InspectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspections", x => x.Id);
                    table.CheckConstraint("CK_QualityInspections_Decision", "([Status] = 'Pending' AND [InspectedByUserId] IS NULL AND [InspectedAtUtc] IS NULL) OR ([Status] IN ('Pass','Fail') AND [InspectedByUserId] IS NOT NULL AND [InspectedAtUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_QualityInspections_AppUsers_InspectedByUserId",
                        column: x => x.InspectedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_StockLots_StockLotId",
                        column: x => x.StockLotId,
                        principalTable: "StockLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoodsReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoodsReceipts_AppUsers_PostedByUserId",
                        column: x => x.PostedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceipts_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceipts_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CancelledQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderItems", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrderItems_Amounts", "[Quantity] > 0 AND [CancelledQuantity] >= 0 AND [CancelledQuantity] <= [Quantity] AND [UnitCost] >= 0");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderItems_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptItems_GoodsReceiptId_PurchaseOrderItemId",
                table: "GoodsReceiptItems",
                columns: new[] { "GoodsReceiptId", "PurchaseOrderItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptItems_PurchaseOrderItemId",
                table: "GoodsReceiptItems",
                column: "PurchaseOrderItemId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GoodsReceiptItems_Quantity",
                table: "GoodsReceiptItems",
                sql: "[ReceivedQuantity] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipts_DepartmentId_PostedAtUtc_Id",
                table: "GoodsReceipts",
                columns: new[] { "DepartmentId", "PostedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipts_Number",
                table: "GoodsReceipts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipts_PostedByUserId",
                table: "GoodsReceipts",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipts_PurchaseOrderId",
                table: "GoodsReceipts",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderItems_ProductId",
                table: "PurchaseOrderItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderItems_PurchaseOrderId_ProductId",
                table: "PurchaseOrderItems",
                columns: new[] { "PurchaseOrderId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_ApprovedByUserId",
                table: "PurchaseOrders",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CreatedByUserId",
                table: "PurchaseOrders",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_DepartmentId_Status_CreatedAtUtc_Id",
                table: "PurchaseOrders",
                columns: new[] { "DepartmentId", "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_Number",
                table: "PurchaseOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_SupplierId",
                table: "PurchaseOrders",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_DepartmentId_Status_Id",
                table: "QualityInspections",
                columns: new[] { "DepartmentId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_InspectedByUserId",
                table: "QualityInspections",
                column: "InspectedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_StockLotId",
                table: "QualityInspections",
                column: "StockLotId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GoodsReceiptItems_GoodsReceipts_GoodsReceiptId",
                table: "GoodsReceiptItems",
                column: "GoodsReceiptId",
                principalTable: "GoodsReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GoodsReceiptItems_PurchaseOrderItems_PurchaseOrderItemId",
                table: "GoodsReceiptItems",
                column: "PurchaseOrderItemId",
                principalTable: "PurchaseOrderItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER TR_StockLots_QualityGuard ON StockLots
                AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted l JOIN deleted d ON d.Id = l.Id
                               WHERE d.QualityStatus <> 'Pending' AND l.QualityStatus <> d.QualityStatus)
                        THROW 51001, 'Final lot quality is immutable.', 1;
                    IF EXISTS (SELECT 1 FROM inserted l JOIN InventoryBalances b ON b.StockLotId = l.Id
                               WHERE l.QualityStatus <> 'Released' AND b.Reserved <> 0)
                        THROW 51001, 'Reserved stock must remain released.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER TR_StockLots_QualityGuard ON StockLots
                AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted l JOIN InventoryBalances b ON b.StockLotId = l.Id
                               WHERE l.QualityStatus <> 'Released' AND b.Reserved <> 0)
                        THROW 51001, 'Reserved stock must remain released.', 1;
                END
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_GoodsReceiptItems_GoodsReceipts_GoodsReceiptId",
                table: "GoodsReceiptItems");

            migrationBuilder.DropForeignKey(
                name: "FK_GoodsReceiptItems_PurchaseOrderItems_PurchaseOrderItemId",
                table: "GoodsReceiptItems");

            migrationBuilder.DropTable(
                name: "GoodsReceipts");

            migrationBuilder.DropTable(
                name: "PurchaseOrderItems");

            migrationBuilder.DropTable(
                name: "QualityInspections");

            migrationBuilder.DropTable(
                name: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_GoodsReceiptItems_GoodsReceiptId_PurchaseOrderItemId",
                table: "GoodsReceiptItems");

            migrationBuilder.DropIndex(
                name: "IX_GoodsReceiptItems_PurchaseOrderItemId",
                table: "GoodsReceiptItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GoodsReceiptItems_Quantity",
                table: "GoodsReceiptItems");

            migrationBuilder.DropColumn(
                name: "GoodsReceiptId",
                table: "GoodsReceiptItems");

            migrationBuilder.DropColumn(
                name: "PurchaseOrderItemId",
                table: "GoodsReceiptItems");

            migrationBuilder.DropColumn(
                name: "ReceivedQuantity",
                table: "GoodsReceiptItems");
        }
    }
}
