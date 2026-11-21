import * as React from "react";
import { m, useReducedMotion } from "motion/react";

/**
 * Fades and lifts its content a little the first time it scrolls into view. With "reduce motion" set in the operating
 * system nothing moves and the content is simply there.
 */
export function Reveal({ children, delay = 0, className }: { children: React.ReactNode; delay?: number; className?: string }) {
  const reduce = useReducedMotion();
  if (reduce) return <div className={className}>{children}</div>;
  return (
    <m.div
      className={className}
      initial={{ opacity: 0, y: 24 }}
      whileInView={{ opacity: 1, y: 0 }}
      viewport={{ once: true, margin: "0px 0px -10% 0px" }}
      transition={{ duration: 0.7, delay, ease: [0.22, 1, 0.36, 1] }}
    >
      {children}
    </m.div>
  );
}

/** Section heading block: a short eyebrow, a large title and an optional lede. */
export function SectionHeading({
  id,
  eyebrow,
  title,
  children,
  className,
}: {
  id: string;
  eyebrow: string;
  title: React.ReactNode;
  children?: React.ReactNode;
  className?: string;
}) {
  return (
    <Reveal className={className}>
      <p className="text-[0.9375rem] font-semibold text-link">{eyebrow}</p>
      <h2 id={id} className="mt-2 max-w-3xl text-[2rem] leading-[1.1] font-bold tracking-[-0.025em] text-balance sm:text-5xl">
        {title}
      </h2>
      {children ? <div className="mt-5 max-w-2xl text-lg leading-relaxed text-pretty text-muted-foreground sm:text-xl">{children}</div> : null}
    </Reveal>
  );
}
