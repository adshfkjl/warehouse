using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Services;
using System.Threading.Tasks;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models.Dtos;
using System.Net;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.API.Models;

namespace PLCManagement.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LocationsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public LocationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Get all locations for a specific PLC
        /// </summary>
        /// <param name="plcId">The ID of the PLC</param>
        /// <returns>List of locations for the specified PLC</returns>
        [HttpGet("by-plc/{plcId}")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IEnumerable<LocationManagementDto>))]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetLocationsByPlcId(string plcId)
        {
            var locations = await _context.LocationManagements
                .Where(l => l.PLCID == plcId)
                .Select(l => new LocationManagementDto
                {
                    Id = l.Id,
                    PLCID = l.PLCID,
                    Shelf = l.Shelf,
                    Row = l.Row,
                    Lev = l.Lev,
                    Position = l.Position,
                    Tray = l.Tray,
                    ShelfStatus = l.ShelfStatus
                })
                .ToListAsync();

            if (!locations.Any())
            {
                return NotFound($"No locations found for PLC with ID: {plcId}");
            }

            return Ok(locations);
        }

        /// <summary>
        /// Get a specific location by PLC ID and position details
        /// </summary>
        /// <param name="plcId">The ID of the PLC</param>
        /// <param name="shelf">Shelf number</param>
        /// <param name="position">Position number</param>
        /// <returns>The requested location</returns>
        [HttpGet("by-position/{plcId}/{shelf}/{position}")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(LocationManagementDto))]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetLocationByPosition(string plcId, int shelf, int position)
        {
            var location = await _context.LocationManagements
                .Where(l => l.PLCID == plcId && l.Shelf == shelf && l.Position == position)
                .Select(l => new LocationManagementDto
                {
                    Id = l.Id,
                    PLCID = l.PLCID,
                    Shelf = l.Shelf,
                    Row = l.Row,
                    Lev = l.Lev,
                    Position = l.Position,
                    Tray = l.Tray,
                    ShelfStatus = l.ShelfStatus
                })
                .FirstOrDefaultAsync();

            if (location == null)
            {
                return NotFound($"Location not found for PLC ID: {plcId}, Shelf: {shelf}, Position: {position}");
            }

            return Ok(location);
        }
    }
}
