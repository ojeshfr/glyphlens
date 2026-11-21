import { renderSources, type RenderName, type RenderTheme } from "@/lib/renders";
import { cn } from "@/lib/utils";

type Props = {
  name: RenderName;
  theme: RenderTheme;
  alt: string;
  /** The `sizes` attribute: how wide the image is drawn at each viewport width. */
  sizes: string;
  priority?: boolean;
  className?: string;
};

/** A real app render as AVIF with a WebP fallback; width and height are set so the layout never shifts. */
export function RenderImage({ name, theme, alt, sizes, priority = false, className }: Props) {
  const src = renderSources(theme, name);
  return (
    <picture>
      <source type="image/avif" srcSet={src.avif} sizes={sizes} />
      <source type="image/webp" srcSet={src.webp} sizes={sizes} />
      <img
        src={src.fallback}
        alt={alt}
        width={src.width}
        height={src.height}
        loading={priority ? "eager" : "lazy"}
        decoding={priority ? "sync" : "async"}
        fetchPriority={priority ? "high" : "auto"}
        className={cn("block h-auto w-full", className)}
      />
    </picture>
  );
}
