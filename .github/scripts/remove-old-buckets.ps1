# Requires: AWS Tools for PowerShell (Install-Module AWS.Tools.S3 -Scope CurrentUser)

param (
    [string]$Prefix = "consumer-output-big-data-query-analytics-",        # Bucket name prefix
    [int]$MaxAgeDays = 2              # Maximum age in days
)

Write-Host "Installing AWS.Tools.S3..."
Set-PSRepository -Name 'PSGallery' -InstallationPolicy Trusted
Install-Module AWS.Tools.S3 -Scope CurrentUser

Write-Host "Fetching AWS credentials..."
$awsAccessKeyId = az keyvault secret show  --vault-name azcleanroomemukv -n aws-access-key-id --query value -o tsv
$awsSecretAccessKey = az keyvault secret show  --vault-name azcleanroomemukv -n aws-secret-access-key --query value -o tsv
Set-AWSCredential -AccessKey $awsAccessKeyId -SecretKey $awsSecretAccessKey -StoreAs default

Import-Module AWS.Tools.S3

$now = Get-Date
Write-Host "Fetching buckets..."
$buckets = Get-S3Bucket
Write-Host "Found $($buckets.Count) buckets. Checking for old buckets..."

foreach ($bucket in $buckets) {
    $bucketName = $bucket.BucketName
    $creationDate = $bucket.CreationDate

    if ($bucketName -like "$Prefix*") {
        $age = ($now - $creationDate).Days

        if ($age -gt $MaxAgeDays) {
            Write-Host "Deleting bucket: $bucketName (Age: $age days)"

            $bucketRegion = (Get-S3BucketLocation -BucketName $bucketName).Value
            # Step 1: Delete all objects
            try {
                $objects = Get-S3Object -BucketName $bucketName -Region $bucketRegion -ErrorAction SilentlyContinue
                foreach ($obj in $objects) {
                    Remove-S3Object -BucketName $bucketName -Key $obj.Key -Region $bucketRegion -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                Write-Warning "Failed to delete objects in ${bucketName}: $_"
            }

            # Step 2: Delete all object versions (if versioning is enabled)
            try {
                $versions = Get-S3ObjectVersion -BucketName $bucketName -Region $bucketRegion -ErrorAction SilentlyContinue
                foreach ($version in $versions) {
                    Remove-S3Object -BucketName $bucketName `
                        -Key $version.Key `
                        -VersionId $version.VersionId `
                        -Region $bucketRegion -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                Write-Verbose "Skipping versioned delete (not enabled?)"
            }

            # Step 3: Delete the bucket
            try {
                Remove-S3Bucket -BucketName $bucketName -Region $bucketRegion -Force -ErrorAction Stop
                Write-Host "Deleted bucket: $bucketName"
            }
            catch {
                Write-Error "Failed to delete bucket ${bucketName}: $_"
            }
        }
    }
}
