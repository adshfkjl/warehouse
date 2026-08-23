using PLCManagement.API.Models;

namespace PLCManagement.API.Interfaces
{
    public interface IDocumentUpLoadService
    {
        Task<DocumentOperationResult> ProcessDocumentUpLoadAsync(string documentNo, string storageLocation);
    }
}