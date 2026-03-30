-- Migration: Add EmissionLoadImage column to DeploymentLayers
-- Date: 2026-03-30
-- Description: Records which EmissionLoad container image was used for layer execution audit

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'DeploymentLayers' AND column_name = 'EmissionLoadImage'
    ) THEN
        ALTER TABLE "DeploymentLayers" ADD COLUMN "EmissionLoadImage" text NULL;
    END IF;
END $$;
