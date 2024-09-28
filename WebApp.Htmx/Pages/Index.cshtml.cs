using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebApp.Htmx.Pages
{
    public record Movie(string Title, string Year, string ImdbID, string Type, string Poster);
    public class IndexModel : PageModel
    {
        private readonly HttpClient _httpClient;
        readonly IAntiforgery _antiforgery;
        public string? RequestToken { get; set; }

        public IndexModel(IHttpClientFactory factory, IAntiforgery antiforgery)
        {
            _httpClient = factory.CreateClient();
            _antiforgery = antiforgery;
        }

        public void OnGet()
        {
            var tokenSet = _antiforgery.GetAndStoreTokens(HttpContext);

            RequestToken = tokenSet.RequestToken;
        }

        [BindProperty] public string SearchText { get; set; }
        public List<Movie> Movies { get; set; }

        public async Task<PartialViewResult> OnPostSearch()
        {
            Movies = new();
            var result = await _httpClient.GetStringAsync($"https://www.omdbapi.com/?apikey=5bf68d19&s={SearchText}");
            var json = JsonObject.Parse(result);
            foreach (var movie in json["Search"].AsArray())
            {
                Movies.Add(new(movie["Title"].ToString(),movie["Year"].ToString(),movie["imdbID"].ToString(),movie["Type"].ToString(),movie["Poster"].ToString()));
            }

            return Partial("_searchResult", Movies);
        }
        
        public IActionResult OnGetShowPoster(string posterUrl)
        {
            // Return HTML for the image using the provided poster URL
            return Content($"<img src='{posterUrl}' alt='Movie Poster' style='max-width: 200px;' />", "text/html");
        }
    }
}