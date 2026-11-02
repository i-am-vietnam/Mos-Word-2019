using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MosWord2019.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionLifetimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "Sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ModeSelectedAt",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ModeSelectedAt",
                table: "Sessions");
        }
    }
}
