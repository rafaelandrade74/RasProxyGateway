using Microsoft.AspNetCore.Mvc;
using TunnelServer.Services;

namespace TunnelServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly TunnelManager _tunnelManager;

    public StatusController(TunnelManager tunnelManager)
    {
        _tunnelManager = tunnelManager;
    }

    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        clients = _tunnelManager.ClientCount,
        tunnels = _tunnelManager.TunnelCount
    });
}
