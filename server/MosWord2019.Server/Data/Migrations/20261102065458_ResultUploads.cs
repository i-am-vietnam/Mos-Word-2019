using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MosWord2019.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResultUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultSigningKey",
                table: "Sessions",
                type: "character varying(44)",
                maxLength: 44,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FlagsJson",
                table: "Results",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "OfflineGapsJson",
                table: "Results",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "Passed",
                table: "Results",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PayloadHash",
                table: "Results",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Score",
                table: "Results",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WrongTasksJson",
                table: "Results",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
            migrationBuilder.Sql("UPDATE \"Sessions\" SET \"ResultSigningKey\" = encode(sha256(uuid_send(gen_random_uuid()) || uuid_send(gen_random_uuid())), 'base64') WHERE \"ResultSigningKey\" = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultSigningKey",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "FlagsJson",
                table: "Results");

            migrationBuilder.DropColumn(
                name: "OfflineGapsJson",
                table: "Results");

            migrationBuilder.DropColumn(
                name: "Passed",
                table: "Results");

            migrationBuilder.DropColumn(
                name: "PayloadHash",
                table: "Results");

            migrationBuilder.DropColumn(
                name: "Score",
                table: "Results");

            migrationBuilder.DropColumn(
                name: "WrongTasksJson",
                table: "Results");
        }
    }
}
