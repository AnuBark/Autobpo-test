using System.Net.Http.Json;

public class DashboardService
{
    private readonly HttpClient _http;
    public DashboardService(HttpClient http) => _http = http;

    public async Task<DashboardOverview> GetOverviewAsync()
    {
        return await _http.GetFromJsonAsync<DashboardOverview>("api/dashboard/overview")
               ?? new DashboardOverview(0, 0, 0, 0);
    }
}
