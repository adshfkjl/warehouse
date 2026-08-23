using PLCManagement.API.Models;

// Services/IDocumentUnloadService.cs
namespace PLCManagement.API.Interfaces
{
    public interface IDocumentDownLoadService
    {
        Task<DocumentOperationResult> ProcessDocumentDownLoadAsync(string documentNo, string loadingPoint);
    }
}

