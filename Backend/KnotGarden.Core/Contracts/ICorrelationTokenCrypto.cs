namespace KnotGarden.Core.Contracts;

public interface ICorrelationTokenCrypto
{
    string GenerateRawToken();
    string HashToken(string rawToken);
}
