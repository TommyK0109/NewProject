using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSharp2.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ShipmentTrackingNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TrackingNotes",
                table: "Shipments",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [Shipments] WHERE [TrackingNotes] <> '')
                    THROW 51004, 'Tracking notes cannot be removed while shipment notes exist. Restore an appropriate backup instead of removing operational history.', 1;
                """);
            migrationBuilder.DropColumn(
                name: "TrackingNotes",
                table: "Shipments");
        }
    }
}
