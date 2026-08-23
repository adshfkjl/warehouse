
// PLCManagement.API/Models/LocationManagement.cs
using System;
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models
{

    public class LocationManagement
    {
        [Key]
        public int Id { get; set; }

        public required string PLCID { get; set; }
        public int Shelf { get; set; }
        public int Row { get; set; }
        public int Lev { get; set; }

        public required int Position { get; set; }

        public string? Tray { get; set; }
        public int ShelfStatus { get; set; }
    }


    public class LocationManagementDto
    {
        public int Id { get; set; }
        public string PLCID { get; set; }
        public int Shelf { get; set; }
        public int Row { get; set; }
        public int Lev { get; set; }
        public int Position { get; set; }
        public string? Tray { get; set; }
        public int ShelfStatus { get; set; }
    }

}

