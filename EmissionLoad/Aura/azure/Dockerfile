FROM mcr.microsoft.com/azure-cli:latest AS base

RUN apk add --no-cache jq curl bash

WORKDIR /aura

COPY entrypoint.sh /aura/entrypoint.sh
RUN chmod +x /aura/entrypoint.sh

RUN adduser -D -s /bin/bash aura
USER aura

ENTRYPOINT ["/aura/entrypoint.sh"]
