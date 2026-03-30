FROM google/cloud-sdk:slim AS base

RUN apt-get update && apt-get install -y --no-install-recommends jq curl bash \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /aura

COPY entrypoint.sh /aura/entrypoint.sh
RUN chmod +x /aura/entrypoint.sh

RUN useradd -m -s /bin/bash aura
USER aura

ENTRYPOINT ["/aura/entrypoint.sh"]
