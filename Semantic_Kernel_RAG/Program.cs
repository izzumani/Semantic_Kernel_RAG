using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AzureAISearch;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Storage.Blobs;
using System.IO;
#pragma warning disable SKEXP0003, SKEXP0011, SKEXP0052, SKEXP0001, SKEXP0050 , SKEXP0010,SKEXP0020
public class Program
{
    private static Kernel _kernel;
    private static SecretClient keyVaultClient;
    private static ISemanticTextMemory memoryWithCustomDb;
    private static BlobServiceClient _blobServiceClient;
    private static BlobContainerClient _containerClient;
    public async static Task Main(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder()
                     .AddUserSecrets<Program>()
                     .Build();

        string? appTenant = config["appTenant"];
        string? appId = config["appId"] ?? null;
        string? appPassword = config["appPassword"] ?? null;
        string? keyVaultName = config["KeyVault"] ?? null;
        string indexName = config["SEARCH_INDEX_NAME"];
        string searchServiceAdminKey = config["SEARCH_ADMIN_KEY"];
        string searchServiceName = config["SEARCH_SERVICE_NAME"];

        string vectorSearchProfileName = "my-vector-profile";
        string vectorSearchHnswConfig = "my-hsnw-vector-config";

        var keyVaultUri = new Uri($"https://{keyVaultName}.vault.azure.net/");
        ClientSecretCredential credential = new ClientSecretCredential(appTenant, appId, appPassword);
        keyVaultClient = new SecretClient(keyVaultUri, credential);
        string? apiKey = keyVaultClient.GetSecret("OpenAIapiKey").Value.Value;
        string? orgId = keyVaultClient.GetSecret("OpenAIorgId").Value.Value;

        var _builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion("gpt-3.5-turbo", apiKey, orgId, serviceId: "gpt35")
            .AddOpenAIChatCompletion("gpt-4", apiKey, orgId, serviceId: "gpt4");

        _kernel = _builder.Build();
        
        AzureKeyCredential azureKeycredential = new AzureKeyCredential(searchServiceAdminKey);
        SearchIndexClient indexClient = new SearchIndexClient(new Uri(searchServiceName), credential);
        indexClient.DeleteIndex(indexName);
        var fields = new FieldBuilder().Build(typeof(SearchModel));
        SearchIndex index = new SearchIndex(indexName)
        {
            Fields = fields,
            VectorSearch = new()
            {
                Profiles =
    {
        new VectorSearchProfile(vectorSearchProfileName, vectorSearchHnswConfig)
    },
                Algorithms =
    {
        new HnswAlgorithmConfiguration(vectorSearchHnswConfig)
    }
            },

        };
        var result = indexClient.CreateOrUpdateIndex(index);

        memoryWithCustomDb = new MemoryBuilder()
                .WithOpenAITextEmbeddingGeneration("text-embedding-3-small", apiKey)
                    .WithMemoryStore(new AzureAISearchMemoryStore(searchServiceName, searchServiceAdminKey))
                        .Build();

        _blobServiceClient = new BlobServiceClient(keyVaultClient.GetSecret("StorageConnectionString").Value.Value);
        _containerClient = _blobServiceClient.GetBlobContainerClient("semantic-kernel");
        await StoreDocumentToAzureAISearchIndex(indexName, "papers/ai_arxiv_202101.json");

        string query_string = "models with long context windows lose information in the middle";

        IAsyncEnumerable<MemoryQueryResult> memories = memoryWithCustomDb.SearchAsync(indexName, query_string, limit: 5, minRelevanceScore: 0.0);
        int i = 0;
        await foreach (MemoryQueryResult item in memories)
        {
            i++;
            Console.WriteLine($"{i}. {item.Metadata.Description}");
        }


        Console.ReadLine();

        

    }

    public static async Task StoreDocumentToAzureAISearchIndex(string searchIndexName, string filePath)
    {
        //string data = File.ReadAllText("ai_arxiv.json");
        string data = string.Empty;
        var blobClient = _containerClient.GetBlobClient(filePath);
        using (var memoryStream = new MemoryStream())
        {
            await blobClient.DownloadToAsync(memoryStream);
           
            var contentType = blobClient.GetProperties().Value.ContentType;
          
            memoryStream.Position = 0;
            StreamReader reader = new StreamReader(memoryStream);
            data = reader.ReadToEnd();


        }
            

        
        int i = 0;
        foreach (string line in data.Split('\n'))
        {
            i++;
            var paper = JsonSerializer.Deserialize<Dictionary<string, object>>(line);
            if (paper == null)
            {
                continue;
            }
            string title = paper["title"]?.ToString() ?? "No title available";
            string id = paper["id"]?.ToString() ?? "No ID available";
            string abstractText = paper["abstract"]?.ToString() ?? "No abstract available";
            id = id.Replace(".", "_");

            await memoryWithCustomDb.SaveInformationAsync(collection: searchIndexName,text: abstractText,id: id,description: title);
            if (i % 100 == 0)
            {
                Console.WriteLine($"Processed {i} documents at {DateTime.Now}");
            }

            if(i==1000)
            {
                break;
            }
        }
    }

}