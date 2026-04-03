# Cleanroom Spark Frontend Helm Chart

This directory contains the Helm chart for deploying the Cleanroom Spark Frontend to Kubernetes.

## Deployment

Deploy the application to a Kubernetes cluster:

```bash
helm upgrade --install cleanroom-spark-frontend ./helm/chart \
  --namespace default \
  --create-namespace \
  --values values.app.yaml \
  --set image.repository=your-registry/cleanroom-cluster/cleanroom-spark-frontend \
  --set image.digest=sha256:yourdigest \
  --set settings.cleanroom.registryUrl=your-registry \
  --set settings.cleanroom.versionsDigest=latest
```

### Customizing the Deployment

The chart can be customized using values passed to the `helm upgrade` command or by creating a custom values file:

```bash
helm upgrade --install cleanroom-spark-frontend ./helm/chart \
  -f my-custom-values.yaml
```

## Permissions

The application requires permissions to manage SparkApplications in the Kubernetes cluster. The chart includes:

- A ServiceAccount for the application
- A ClusterRole that grants permissions specifically for SparkApplications
- A ClusterRoleBinding that binds the service account to this role

This ensures that the frontend has the necessary permissions to create, update, and monitor Spark jobs across multiple namespaces.
