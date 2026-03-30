FROM mcr.microsoft.com/azure-cli:latest AS base

# jq, curl, bash already pre-installed on Azure Linux
RUN tdnf install -y shadow-utils && tdnf clean all

WORKDIR /aura

COPY entrypoint.sh /aura/entrypoint.sh
RUN chmod +x /aura/entrypoint.sh

RUN useradd -m -s /bin/bash aura
USER aura

ENTRYPOINT ["/aura/entrypoint.sh"]
