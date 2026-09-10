using HexagonLlamaCppSharp.Maui.Models;

namespace HexagonLlamaCppSharp.Maui.Services;

public interface IModelFilePicker
{
    Task<ModelFile?> PickAsync();
}