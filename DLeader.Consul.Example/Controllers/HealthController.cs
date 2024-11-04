using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    [Route("/health")]
    public IActionResult Check()
    {
        return Ok("Healthy");
    }
}