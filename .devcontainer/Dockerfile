
#https://code.visualstudio.com/docs/devcontainers/create-dev-container#_dockerfile
FROM mcr.microsoft.com/devcontainers/universal:3.0.3-noble
RUN wget -O azcopy_v10.tar.gz https://aka.ms/downloadazcopy-v10-linux && \
    tar -xf azcopy_v10.tar.gz --strip-components=1 && \
    rm azcopy_v10.tar.gz && \
    sudo mv azcopy /usr/bin

RUN wget https://github.com/derailed/k9s/releases/latest/download/k9s_linux_amd64.deb && \
    sudo apt install ./k9s_linux_amd64.deb && \
    rm k9s_linux_amd64.deb