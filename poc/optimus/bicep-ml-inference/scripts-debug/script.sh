      #!/bin/bash
      set -e
      # set -x  # Enable command tracing
      
      echo "========================================="
      echo "Helm Chart Installation: $CHART_NAME"
      echo "========================================="
      echo "Resource Group: $RG"
      echo "AKS Cluster: $AKS_NAME"
      echo "Namespace: $NAMESPACE"
      echo "Chart Name: $CHART_NAME"
      echo "Release Name: $RELEASE_NAME"
      echo "Chart Version: $CHART_VERSION"
      echo "Repository URL: $REPO_URL"
      echo "Initial Delay: $INITIAL_DELAY"
      echo "========================================="
      
      echo "⏰ Sleeping for $INITIAL_DELAY seconds..."
      sleep ${INITIAL_DELAY//s/}
      
      echo "🔐 Getting AKS credentials..."
      az aks get-credentials --resource-group "$RG" --name "$AKS_NAME" --overwrite-existing
      
      echo "📦 Installing tar and awk (required for Helm)..."
      curl -L https://busybox.net/downloads/binaries/1.35.0-x86_64-linux-musl/busybox -o /usr/local/bin/busybox
      chmod +x /usr/local/bin/busybox
      ln -sf /usr/local/bin/busybox /usr/local/bin/tar
      ln -sf /usr/local/bin/busybox /usr/local/bin/awk
      export PATH="/usr/local/bin:$PATH"
      
      echo "📦 Installing Helm..."
      curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
      
      echo "📋 Verifying Helm installation..."
      helm version
      
      # Prepare values file if provided
      if [[ -n "$HELM_VALUES" ]]; then
        echo "📝 Creating values file..."
        echo "Values content:"
        echo "$HELM_VALUES"
        echo "$HELM_VALUES" > values.yaml
        HELM_ARGS="$HELM_ARGS -f values.yaml"
        echo "Updated HELM_ARGS: $HELM_ARGS"
      fi
      
      # Detect repository type and handle accordingly
      if [[ "$REPO_URL" == oci://* ]]; then
        echo "🔗 Detected OCI repository: $REPO_URL"
        echo "🚀 Installing $CHART_NAME from OCI repository..."
        
        # For OCI repositories, use the full OCI URL directly
        CHART_REFERENCE="$REPO_URL/$CHART_NAME"
        
        echo "⚙️  Installing/upgrading chart from OCI..."
        helm upgrade --install "$RELEASE_NAME" "$CHART_REFERENCE" \
          --version "$CHART_VERSION" \
          --namespace "$NAMESPACE" \
          --wait \
          $HELM_ARGS
          
      else
        echo "🔗 Detected traditional Helm repository: $REPO_URL"
        echo "Adding Helm repository..."
        helm repo add chart-repo "$REPO_URL"
        
        echo "🔄 Updating Helm repositories..."
        helm repo update
        
        echo "🚀 Installing $CHART_NAME from traditional repository..."
        
        echo "⚙️  Installing/upgrading chart..."
        helm upgrade --install "$RELEASE_NAME" chart-repo/"$CHART_NAME" \
          --version "$CHART_VERSION" \
          --namespace "$NAMESPACE" \
          --wait \
          $HELM_ARGS
      fi
      
      echo "========================================="
      echo "✅ Helm chart installation completed!"
      echo "========================================="