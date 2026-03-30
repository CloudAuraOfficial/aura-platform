FROM amazon/aws-cli:latest AS base

RUN yum install -y jq bash shadow-utils && yum clean all

WORKDIR /aura

COPY entrypoint.sh /aura/entrypoint.sh
RUN chmod +x /aura/entrypoint.sh

RUN useradd -m -s /bin/bash aura
USER aura

ENTRYPOINT ["/aura/entrypoint.sh"]
