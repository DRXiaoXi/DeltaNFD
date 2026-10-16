using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services;

// CNG 私钥持久保存于当前 Windows 用户的密钥存储，不导出到仓库或策略包。
try
{
    if (args is ["init", var keyName, var publicOutput])
    {
        if (CngKey.Exists(keyName)) throw new InvalidOperationException("密钥已存在，拒绝覆盖。");
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
        {
            KeyUsage = CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None,
        });
        using var signer = new ECDsaCng(key);
        File.WriteAllText(publicOutput, Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        Console.WriteLine("已创建当前用户专用的不可导出签名密钥。公钥：" + Path.GetFullPath(publicOutput));
    }
    else if (args is ["sign", var keyName2, var policyPath, var outputPath])
    {
        using var key = CngKey.Open(keyName2);
        using var signer = new ECDsaCng(key);
        var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        if (!EmergencyPolicyTrust.Keys.TryGetValue(EmergencyPolicyTrust.KeyId, out var pinned) || pinned != publicKey)
            throw new InvalidOperationException("此私钥与客户端内置公钥不符，拒绝发布。");
        var payload = File.ReadAllBytes(policyPath);
        if (payload.Length > EmergencyPolicyVerifier.MaximumPayloadBytes) throw new InvalidDataException("策略过大。");
        var envelope = new EmergencyPolicyEnvelope(EmergencyPolicyTrust.KeyId, Convert.ToBase64String(payload),
            Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, EmergencyPolicyVerifier.JsonOptions);
        var policy = EmergencyPolicyVerifier.Verify(bytes, EmergencyPolicyTrust.Keys, DateTimeOffset.UtcNow);
        if (File.Exists(outputPath))
        {
            var prior = EmergencyPolicyVerifier.Verify(File.ReadAllBytes(outputPath), EmergencyPolicyTrust.Keys,
                DateTimeOffset.UtcNow, requireFresh: false);
            if (prior.Channel != policy.Channel || prior.Sequence >= policy.Sequence)
                throw new InvalidDataException("发布文件通道不符或序号没有递增。");
        }
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine($"已签名 {policy.Channel} #{policy.Sequence} {policy.Id}，尚未上传。");
    }
    else throw new ArgumentException("用法：init <CNG密钥名> <公钥输出文件> | sign <CNG密钥名> <策略JSON> <签名包输出文件>");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
