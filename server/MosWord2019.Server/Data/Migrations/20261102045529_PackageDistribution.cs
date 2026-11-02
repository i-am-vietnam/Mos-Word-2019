using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MosWord2019.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageDistribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExamDefinitionHash",
                table: "Sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExamSeed",
                table: "Sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExamSnapshotJson",
                table: "Sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedDefinitionHash",
                table: "Sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogEntryJson",
                table: "Packages",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "Packages",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TasksJson",
                table: "Packages",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "ZipSha256",
                table: "Packages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExamDefinitionHash",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ExamSeed",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ExamSnapshotJson",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ReportedDefinitionHash",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "CatalogEntryJson",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "TasksJson",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "ZipSha256",
                table: "Packages");
        }
    }
}
