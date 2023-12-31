﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RadioStation.Data;

#nullable disable

namespace RadioStation.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20230503095621_first")]
    partial class first
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "7.0.5")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

            modelBuilder.Entity("RadioStation.Models.Radio", b =>
                {
                    b.Property<int>("RadioId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<int>("RadioId"));

                    b.Property<string>("RadioLink")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("RadioName")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.HasKey("RadioId");

                    b.ToTable("Radios");
                });

            modelBuilder.Entity("RadioStation.Models.User", b =>
                {
                    b.Property<int>("UserId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<int>("UserId"));

                    b.Property<string>("Email")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.Property<int?>("EmailConfirmed")
                        .HasColumnType("int");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("Password")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.HasKey("UserId");

                    b.ToTable("Users");
                });

            modelBuilder.Entity("RadioUser", b =>
                {
                    b.Property<int>("FavoriteRadiosRadioId")
                        .HasColumnType("int");

                    b.Property<int>("UserId")
                        .HasColumnType("int");

                    b.HasKey("FavoriteRadiosRadioId", "UserId");

                    b.HasIndex("UserId");

                    b.ToTable("UserFavoriteRadios", (string)null);
                });

            modelBuilder.Entity("RadioUser", b =>
                {
                    b.HasOne("RadioStation.Models.Radio", null)
                        .WithMany()
                        .HasForeignKey("FavoriteRadiosRadioId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("RadioStation.Models.User", null)
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });
#pragma warning restore 612, 618
        }
    }
}
