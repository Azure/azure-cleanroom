# Shared helper functions for kserve-inferencing test scripts.

function Ensure-RoleAssignment($assigneeObjectId, $scope, $role, $principalType = "ServicePrincipal") {
    $existing = (az role assignment list `
        --assignee-object-id $assigneeObjectId `
        --scope $scope `
        --role $role `
        --fill-principal-name false `
        --fill-role-definition-name false) | ConvertFrom-Json

    if ($existing.Length -eq 1) {
        Write-Host "'$role' permission already exists, skipping assignment."
    }
    else {
        Write-Host "Assigning '$role'..."
        az role assignment create `
            --role $role `
            --scope $scope `
            --assignee-object-id $assigneeObjectId `
            --assignee-principal-type $principalType
    }
}
