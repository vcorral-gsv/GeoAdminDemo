using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace GeoAdminDemo.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAreaSpatialGeometry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LevelLabel",
                table: "AdminAreas",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "AdminAreas",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<Geometry>(
                name: "Geometry",
                table: "AdminAreas",
                type: "geography",
                nullable: true).Annotation("SqlServer:SpatialReferenceSystemId", 4326);

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AdminAreas_Geometry' AND object_id = OBJECT_ID('dbo.AdminAreas'))
                BEGIN
                    CREATE SPATIAL INDEX IX_AdminAreas_Geometry
                    ON dbo.AdminAreas(Geometry)
                    USING GEOGRAPHY_AUTO_GRID;
                END
                ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Geometry",
                table: "AdminAreas");

            migrationBuilder.AlterColumn<string>(
                name: "LevelLabel",
                table: "AdminAreas",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1024)",
                oldMaxLength: 1024,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "AdminAreas",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512);
        }
    }
}
