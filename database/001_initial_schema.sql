CREATE EXTENSION IF NOT EXISTS postgis;

CREATE TABLE IF NOT EXISTS spatial_features (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    feature_type VARCHAR(32) NOT NULL,
    block_name VARCHAR(255),
    source_file VARCHAR(1024),
    layer_name VARCHAR(255),
    attributes JSONB NOT NULL DEFAULT '{}'::jsonb,
    geom GEOMETRY(Geometry, 5186) NOT NULL,
    outer_geom GEOMETRY(Geometry, 5186),
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT spatial_features_type_check CHECK (GeometryType(geom) IN ('POINT', 'LINESTRING', 'POLYGON', 'GEOMETRYCOLLECTION')),
    CONSTRAINT spatial_features_srid_check CHECK (ST_SRID(geom) = 5186),
    CONSTRAINT spatial_features_valid_check CHECK (ST_IsValid(geom))
);

CREATE INDEX IF NOT EXISTS idx_spatial_features_geom ON spatial_features USING GIST (geom);
CREATE INDEX IF NOT EXISTS idx_spatial_features_outer_geom ON spatial_features USING GIST (outer_geom);
CREATE INDEX IF NOT EXISTS idx_spatial_features_attributes ON spatial_features USING GIN (attributes);

CREATE TABLE IF NOT EXISTS spatial_block_instances (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_file VARCHAR(1024) NOT NULL,
    block_name VARCHAR(255) NOT NULL,
    layer_name VARCHAR(255),
    attributes JSONB NOT NULL DEFAULT '{}'::jsonb,
    geom GEOMETRY(Geometry, 5186) NOT NULL,
    outer_geom GEOMETRY(Geometry, 5186) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_geom ON spatial_block_instances USING GIST (geom);
CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_outer_geom ON spatial_block_instances USING GIST (outer_geom);
CREATE INDEX IF NOT EXISTS idx_spatial_block_instances_layer ON spatial_block_instances (layer_name);