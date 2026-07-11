namespace KnotGarden.Core.Contracts;

public interface ICredentialCipher
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}