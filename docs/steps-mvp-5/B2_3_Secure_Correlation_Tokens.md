# Step 5 — B2.3: Secure Correlation Tokens

## Goal
Implement a cryptographically secure, high-entropy resume correlation token system. Webhook-wait nodes generate these tokens to identify callback requests. To secure the system against database leaks and token sniffing, tokens must be stored strictly in SHA-256 hashed format in SQLite, enforce a configurable Time-To-Live (TTL), and be evaluated transactionally.

---

## Invariant Alignment
* **Invariant 6.2 (Token Hardening):** High-entropy cryptographically secure token, stored strictly as SHA-256 hash in database, has a TTL, and is invalidated/consumed transactionally.

---

## Proposed Changes

### 1. Create [CorrelationToken.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Core/CorrelationToken.cs) [NEW]
Define the C# entity representation:
```csharp
public sealed class CorrelationToken
{
    public Guid Id { get; set; }
    public string HashedToken { get; set; } = null!;
    public Guid ExecutionInstanceId { get; set; }
    public string NodeId { get; set; } = null!;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; } // Tracks usage without immediate deletion gaps
}
```
* Register this new DbSet in `AppDbContext.cs` and generate the EF Core migration to update the SQLite database schemas.

### 2. Implement Token Generator & Hash Utility [NEW]
Create a service class to handle cryptographically secure operations:
* **Generation**: Generate 32 bytes using `RandomNumberGenerator` and format as a URL-safe Base64 string.
* **Hashing**: Hash the Base64 string using `SHA256` before writing to the database `HashedToken` column.

---

## Verification & Test Checklist

### 1. Unit Tests
* Write unit tests in `CorrelationTokenTests.cs` verifying:
  * **Hashing**: Compare the raw generated token with the database record to ensure the raw token is *never* stored in plaintext.
  * **TTL Validation**: Generate a token with a past expiration date and assert the validator rejects it as expired.

### 2. Manual Verification
* Run migrations and verify database columns are updated correctly.
