using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoAdminDemo.Migrations
{
    /// <inheritdoc />
#pragma warning disable CS8981 // The type name only contains lower-cased ascii characters. Such names may become reserved for the language.
    public partial class bbdd : Migration
#pragma warning restore CS8981 // The type name only contains lower-cased ascii characters. Such names may become reserved for the language.
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAreas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CountryIso3 = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LevelLabel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    GeometryWkt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminAreas_AdminAreas_ParentId",
                        column: x => x.ParentId,
                        principalTable: "AdminAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAreas_CountryIso3_Level_Code",
                table: "AdminAreas",
                columns: new[] { "CountryIso3", "Level", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminAreas_CountryIso3_Level_ParentId",
                table: "AdminAreas",
                columns: new[] { "CountryIso3", "Level", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAreas_ParentId",
                table: "AdminAreas",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminAreas");
        }
    }
}
