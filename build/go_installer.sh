#!/bin/bash
work_dir=$(echo $1 | sed 's:/*$::')
version="1.23.8"
arch="amd64"

echo "Installing on : " $arch " Version : " $version
wget "https://golang.org/dl/go$version.linux-$arch.tar.gz" -P "$work_dir"
rm -rf /usr/local/go
tar -C /usr/local -xzf "$work_dir"/go"$version".linux-$arch.tar.gz
ln -sf /usr/local/go/bin/go /usr/bin/go
