using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RadioStation.Migrations
{
    /// <inheritdoc />
    public partial class AddSongEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "EmailConfirmed",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "Songs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Artist = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    YoutubeVideoId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilePath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Duration = table.Column<TimeSpan>(type: "time", nullable: false),
                    RequesterName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AddedToQueueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedPlayingAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsPlaying = table.Column<bool>(type: "bit", nullable: false),
                    IsFinished = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Songs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Songs");

            migrationBuilder.AlterColumn<int>(
                name: "EmailConfirmed",
                table: "Users",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");
        }
    }
}
