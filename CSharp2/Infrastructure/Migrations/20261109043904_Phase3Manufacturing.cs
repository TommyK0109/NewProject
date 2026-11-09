using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSharp2.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Manufacturing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Earlier phases reserved only output identities and exposed no output-posting API.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [ProductionOutputs])
                    THROW 51002, 'Phase 3 requires an empty Phase 2 ProductionOutputs table. Existing placeholder sources need an explicit data migration before upgrading.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements");

            migrationBuilder.AddColumn<Guid>(
                name: "MaterialIssueItemId",
                table: "StockMovements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostedAtUtc",
                table: "ProductionOutputs",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.AddColumn<Guid>(
                name: "PostedByUserId",
                table: "ProductionOutputs",
                type: "uniqueidentifier",
                nullable: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ProducedQuantity",
                table: "ProductionOutputs",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ProductionOrderId",
                table: "ProductionOutputs",
                type: "uniqueidentifier",
                nullable: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ScrapQuantity",
                table: "ProductionOutputs",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false);

            migrationBuilder.CreateTable(
                name: "BillOfMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FinishedProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillOfMaterials", x => x.Id);
                    table.CheckConstraint("CK_BillOfMaterials_Approval", "([Status] = 'Draft' AND [ApprovedByUserId] IS NULL AND [ApprovedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [ApprovedByUserId] IS NOT NULL AND [ApprovedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_BillOfMaterials_Revision", "[Revision] > 0");
                    table.ForeignKey(
                        name: "FK_BillOfMaterials_AppUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillOfMaterials_Products_FinishedProductId",
                        column: x => x.FinishedProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequiredByUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.CheckConstraint("CK_SalesOrders_Status", "[Status] IN ('Draft','Confirmed','Reserved','Dispatched','Completed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_SalesOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BomItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillOfMaterialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuantityPerUnit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomItems", x => x.Id);
                    table.CheckConstraint("CK_BomItems_Quantity", "[QuantityPerUnit] > 0");
                    table.ForeignKey(
                        name: "FK_BomItems_BillOfMaterials_BillOfMaterialId",
                        column: x => x.BillOfMaterialId,
                        principalTable: "BillOfMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BomItems_Products_MaterialProductId",
                        column: x => x.MaterialProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderItems", x => x.Id);
                    table.CheckConstraint("CK_SalesOrderItems_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesOrderItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderItems_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesOrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillOfMaterialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlannedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ScheduledStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ScheduledEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrders", x => x.Id);
                    table.CheckConstraint("CK_ProductionOrders_Quantity", "[PlannedQuantity] > 0");
                    table.CheckConstraint("CK_ProductionOrders_Schedule", "[ScheduledEndUtc] >= [ScheduledStartUtc]");
                    table.CheckConstraint("CK_ProductionOrders_Status", "[Status] IN ('Draft','Released','InProduction','AwaitingQC','Completed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_ProductionOrders_BillOfMaterials_BillOfMaterialId",
                        column: x => x.BillOfMaterialId,
                        principalTable: "BillOfMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_SalesOrderItems_SalesOrderItemId",
                        column: x => x.SalesOrderItemId,
                        principalTable: "SalesOrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductionOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialIssues_AppUsers_PostedByUserId",
                        column: x => x.PostedByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialIssues_ProductionOrders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionRequirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductionOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequiredQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionRequirements", x => x.Id);
                    table.CheckConstraint("CK_ProductionRequirements_Quantity", "[RequiredQuantity] > 0");
                    table.ForeignKey(
                        name: "FK_ProductionRequirements_ProductionOrders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionRequirements_Products_MaterialProductId",
                        column: x => x.MaterialProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialIssueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialIssueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductionRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StockLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialIssueItems", x => x.Id);
                    table.CheckConstraint("CK_MaterialIssueItems_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_MaterialIssueItems_MaterialIssues_MaterialIssueId",
                        column: x => x.MaterialIssueId,
                        principalTable: "MaterialIssues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialIssueItems_ProductionRequirements_ProductionRequirementId",
                        column: x => x.ProductionRequirementId,
                        principalTable: "ProductionRequirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialIssueItems_StockLots_StockLotId",
                        column: x => x.StockLotId,
                        principalTable: "StockLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_MaterialIssueItemId_Type",
                table: "StockMovements",
                columns: new[] { "MaterialIssueItemId", "Type" },
                unique: true,
                filter: "[MaterialIssueItemId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements",
                sql: "[ReservedDelta] = 0 AND (([Type] = 'Receipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NOT NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NULL) OR ([Type] = 'OutputReceipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NOT NULL AND [MaterialIssueItemId] IS NULL) OR ([Type] = 'MaterialIssue' AND [OnHandDelta] < 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOutputs_PostedByUserId",
                table: "ProductionOutputs",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOutputs_ProductionOrderId",
                table: "ProductionOutputs",
                column: "ProductionOrderId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductionOutputs_Quantities",
                table: "ProductionOutputs",
                sql: "[ProducedQuantity] >= 0 AND [ScrapQuantity] >= 0 AND [ProducedQuantity] + [ScrapQuantity] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterials_ApprovedByUserId",
                table: "BillOfMaterials",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterials_FinishedProductId_Revision",
                table: "BillOfMaterials",
                columns: new[] { "FinishedProductId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BomItems_BillOfMaterialId_MaterialProductId",
                table: "BomItems",
                columns: new[] { "BillOfMaterialId", "MaterialProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BomItems_MaterialProductId",
                table: "BomItems",
                column: "MaterialProductId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialIssueItems_MaterialIssueId_ProductionRequirementId_StockLotId",
                table: "MaterialIssueItems",
                columns: new[] { "MaterialIssueId", "ProductionRequirementId", "StockLotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialIssueItems_ProductionRequirementId",
                table: "MaterialIssueItems",
                column: "ProductionRequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialIssueItems_StockLotId_MaterialIssueId",
                table: "MaterialIssueItems",
                columns: new[] { "StockLotId", "MaterialIssueId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialIssues_PostedByUserId",
                table: "MaterialIssues",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialIssues_ProductionOrderId",
                table: "MaterialIssues",
                column: "ProductionOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_BillOfMaterialId",
                table: "ProductionOrders",
                column: "BillOfMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_DepartmentId_Status_ScheduledStartUtc_Id",
                table: "ProductionOrders",
                columns: new[] { "DepartmentId", "Status", "ScheduledStartUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_Number",
                table: "ProductionOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_SalesOrderItemId",
                table: "ProductionOrders",
                column: "SalesOrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionRequirements_MaterialProductId",
                table: "ProductionRequirements",
                column: "MaterialProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionRequirements_ProductionOrderId_MaterialProductId",
                table: "ProductionRequirements",
                columns: new[] { "ProductionOrderId", "MaterialProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderItems_ProductId",
                table: "SalesOrderItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderItems_SalesOrderId_ProductId",
                table: "SalesOrderItems",
                columns: new[] { "SalesOrderId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId",
                table: "SalesOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_DepartmentId_Status_RequiredByUtc_Id",
                table: "SalesOrders",
                columns: new[] { "DepartmentId", "Status", "RequiredByUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Number",
                table: "SalesOrders",
                column: "Number",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductionOutputs_AppUsers_PostedByUserId",
                table: "ProductionOutputs",
                column: "PostedByUserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductionOutputs_ProductionOrders_ProductionOrderId",
                table: "ProductionOutputs",
                column: "ProductionOrderId",
                principalTable: "ProductionOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_MaterialIssueItems_MaterialIssueItemId",
                table: "StockMovements",
                column: "MaterialIssueItemId",
                principalTable: "MaterialIssueItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [SalesOrders]) OR EXISTS (SELECT 1 FROM [BillOfMaterials])
                    OR EXISTS (SELECT 1 FROM [ProductionOutputs])
                    THROW 51002, 'Phase 3 cannot be downgraded while manufacturing documents exist. Restore an appropriate backup instead of removing operational history.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ProductionOutputs_AppUsers_PostedByUserId",
                table: "ProductionOutputs");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductionOutputs_ProductionOrders_ProductionOrderId",
                table: "ProductionOutputs");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_MaterialIssueItems_MaterialIssueItemId",
                table: "StockMovements");

            migrationBuilder.DropTable(
                name: "BomItems");

            migrationBuilder.DropTable(
                name: "MaterialIssueItems");

            migrationBuilder.DropTable(
                name: "MaterialIssues");

            migrationBuilder.DropTable(
                name: "ProductionRequirements");

            migrationBuilder.DropTable(
                name: "ProductionOrders");

            migrationBuilder.DropTable(
                name: "BillOfMaterials");

            migrationBuilder.DropTable(
                name: "SalesOrderItems");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_MaterialIssueItemId_Type",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_ProductionOutputs_PostedByUserId",
                table: "ProductionOutputs");

            migrationBuilder.DropIndex(
                name: "IX_ProductionOutputs_ProductionOrderId",
                table: "ProductionOutputs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductionOutputs_Quantities",
                table: "ProductionOutputs");

            migrationBuilder.DropColumn(
                name: "MaterialIssueItemId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "PostedAtUtc",
                table: "ProductionOutputs");

            migrationBuilder.DropColumn(
                name: "PostedByUserId",
                table: "ProductionOutputs");

            migrationBuilder.DropColumn(
                name: "ProducedQuantity",
                table: "ProductionOutputs");

            migrationBuilder.DropColumn(
                name: "ProductionOrderId",
                table: "ProductionOutputs");

            migrationBuilder.DropColumn(
                name: "ScrapQuantity",
                table: "ProductionOutputs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_SourceAndDeltas",
                table: "StockMovements",
                sql: "[OnHandDelta] > 0 AND [ReservedDelta] = 0 AND (([Type] = 'Receipt' AND [GoodsReceiptItemId] IS NOT NULL AND [ProductionOutputId] IS NULL) OR ([Type] = 'OutputReceipt' AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NOT NULL))");
        }
    }
}
